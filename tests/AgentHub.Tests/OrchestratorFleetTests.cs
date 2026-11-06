using System.IO;
using AgentHub.Models;
using AgentHub.Services;
using AgentHub.Services.Ideas;
using AgentHub.Services.Orchestrator;
using AgentHub.Services.Usage;
using Xunit;

namespace AgentHub.Tests;

/// <summary>
/// Daniel 2026-11-06: starting a second orchestration suspended the first ("can we make this as many as we want?").
/// Orchestrators now run side by side; a second run on the same repository gets a git worktree of its own.
/// </summary>
public class OrchestratorFleetTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "AgentHub Fleet " + Guid.NewGuid().ToString("n")[..8]);
    private readonly ProcessRunner _runner = new();
    private readonly Dictionary<string, ScriptedBrain> _brains = [];

    public OrchestratorFleetTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_dir, recursive: true);
        }
        catch { }
    }

    private static OrchestratorDecision Send(string text) => new(OrchestratorAction.Send, text, "none", "next step", "building");

    /// <summary>A fleet whose engines use the scripted orchestrator of each idea (by title).</summary>
    private (OrchestratorFleet Fleet, IdeaService Ideas) Setup()
    {
        var ideas = new IdeaService(Path.Combine(_dir, "ideas.json"));
        var fleet = new OrchestratorFleet(ideas, steering => new OrchestratorEngine(ideas, new GitService(_runner), _runner, steering)
        {
            RunsRoot = Path.Combine(_dir, "runs"),
            WorktreesRoot = Path.Combine(_dir, "worktrees"),
            BrainFactory = (record, _) => _brains[record.IdeaTitle],
            UsageReader = () => Task.FromResult<UsageSnapshot?>(null),
            TurnSettle = TimeSpan.Zero,
            WorkerStartupDelay = TimeSpan.Zero,
            LoopInterval = TimeSpan.FromMilliseconds(50),
            IdleRecheck = TimeSpan.FromHours(1),
            DeliveryCheckTimeout = TimeSpan.FromMilliseconds(100)
        }, new SteeringService(Path.Combine(_dir, "steering.json")));
        return (fleet, ideas);
    }

    private async Task<ProjectIdea> AddIdeaAsync(IdeaService ideas, string title, string folder, params OrchestratorDecision[] script)
    {
        var idea = new ProjectIdea { Title = title, Description = $"Build {title}", TargetDirectory = folder, ContextHandoffThreshold = 0.75 };
        await ideas.AddIdeaAsync(idea);
        _brains[title] = new ScriptedBrain(script);
        return idea;
    }

    /// <summary>IdeaService seeds a default idea; the queue must hold only the test's own.</summary>
    private static async Task OnlyTheseIdeas(IdeaService ideas, params ProjectIdea[] keep)
    {
        foreach (var other in (await ideas.LoadAsync()).Where(i => keep.All(k => k.Id != i.Id))) await ideas.DeleteIdeaAsync(other.Id);
    }

    private static async Task Until(Func<bool> condition, string what)
    {
        for (var i = 0; i < 400 && !condition(); i++) await Task.Delay(25);
        Assert.True(condition(), "Timed out waiting for: " + what);
    }

    private List<RunRecord> Runs() =>
        Directory.Exists(Path.Combine(_dir, "runs"))
            ? Directory.GetDirectories(Path.Combine(_dir, "runs")).Select(d => RunRecorder.ReadRecord(Path.Combine(d, "run.json"))).OfType<RunRecord>().ToList()
            : [];

    private async Task Git(string dir, params string[] args)
    {
        var r = await _runner.RunAsync("git", args, dir);
        Assert.True(r.ExitCode == 0, $"git {string.Join(' ', args)}: {r.StdErr}");
    }

    [Fact]
    public async Task Two_ideas_build_side_by_side_and_neither_suspends_the_other()
    {
        var (fleet, ideas) = Setup();
        var a = await AddIdeaAsync(ideas, "Alpha", Path.Combine(_dir, "Alpha"), Send("Build alpha."), Send("Alpha step 2."));
        var b = await AddIdeaAsync(ideas, "Beta", Path.Combine(_dir, "Beta"), Send("Build beta."));
        await OnlyTheseIdeas(ideas, a, b);
        var workerA = new ScriptedWorker();
        var workerB = new ScriptedWorker();
        using (fleet)
        {
            var engineA = await fleet.BuildNowAsync(a, workerA);
            await Until(() => workerA.Received.Count == 1, "Alpha's first instruction");
            var engineB = await fleet.BuildNowAsync(b, workerB);
            await Until(() => workerB.Received.Count == 1, "Beta's first instruction");

            Assert.NotSame(engineA, engineB);
            Assert.Equal(2, fleet.Active.Count);
            Assert.Equal(OrchestratorState.Building, engineA.State);
            Assert.Same(engineA, fleet.EngineFor(a.Id));
            Assert.Same(engineB, fleet.EngineFor(b.Id));
            Assert.All(Runs(), r => Assert.Equal("running", r.Outcome)); // nothing suspended
            Assert.Equal(2, Runs().Count);

            // Each engine answers its own worker only.
            workerA.FinishTurn("Alpha scaffolded.");
            await Until(() => workerA.Received.Count == 2, "Alpha's second instruction");
            Assert.Equal("Alpha step 2.", workerA.Received[1]);
            Assert.Single(workerB.Received);
            Assert.Equal([(ExchangeKind.Sent, "Build beta.")], engineB.Conversation.Select(x => (x.Kind, x.Text)));
        }
    }

    [Fact]
    public async Task A_second_run_on_the_same_repository_gets_its_own_worktree_and_branch()
    {
        var repo = Path.Combine(_dir, "Shared Repo");
        Directory.CreateDirectory(Path.Combine(repo, ".agenthub"));
        await Git(repo, "init", "-b", "main");
        await Git(repo, "config", "user.email", "test@example.com");
        await Git(repo, "config", "user.name", "Test");
        // The repository's committed handoff belongs to the first project; the worktree must not hand it to the second.
        await File.WriteAllTextAsync(Path.Combine(repo, ".agenthub", "handoff.md"), "# Autonomous Project: First\n\nStage 1.13 in progress.\n");
        await File.WriteAllTextAsync(Path.Combine(repo, "README.md"), "shared");
        await Git(repo, "add", ".");
        await Git(repo, "commit", "-m", "start");

        var (fleet, ideas) = Setup();
        var first = await AddIdeaAsync(ideas, "First", repo, Send("Carry on with 1.13."));
        var second = await AddIdeaAsync(ideas, "Second", repo, Send("Start the walkthrough."));
        await OnlyTheseIdeas(ideas, first, second);
        var w1 = new ScriptedWorker();
        var w2 = new ScriptedWorker();
        using (fleet)
        {
            var e1 = await fleet.BuildNowAsync(first, w1);
            await Until(() => w1.Received.Count == 1, "First's instruction");
            var e2 = await fleet.BuildNowAsync(second, w2);
            await Until(() => w2.Received.Count == 1, "Second's instruction");

            Assert.Equal(repo, w1.StartedIn);
            Assert.Null(e1.WorktreeBranch);
            var worktree = e2.WorkingDirectory!;
            Assert.Equal(worktree, w2.StartedIn);
            Assert.StartsWith(Path.Combine(_dir, "worktrees"), worktree);
            Assert.True(File.Exists(Path.Combine(worktree, ".git")), "a git worktree");
            Assert.StartsWith("agenthub/second-", e2.WorktreeBranch);
            Assert.Equal(0, (await _runner.RunAsync("git", ["rev-parse", "--verify", e2.WorktreeBranch!], repo)).ExitCode);

            var record = Runs().Single(r => r.IdeaTitle == "Second");
            Assert.Equal(worktree, record.WorkingDirectory);
            Assert.Equal(e2.WorktreeBranch, record.Branch);
            Assert.StartsWith("# Autonomous Project: Second\n", await File.ReadAllTextAsync(Path.Combine(worktree, ".agenthub", "handoff.md")));
            Assert.Contains("Stage 1.13", await File.ReadAllTextAsync(Path.Combine(worktree, ".agenthub", "handoff-from-branch-point.md")));
            // The first project's folder and handoff are untouched.
            Assert.Contains("Stage 1.13", await File.ReadAllTextAsync(Path.Combine(repo, ".agenthub", "handoff.md")));
        }
    }

    [Fact]
    public async Task The_auto_builder_skips_an_idea_another_orchestrator_is_building()
    {
        var (fleet, ideas) = Setup();
        var a = await AddIdeaAsync(ideas, "Alpha", Path.Combine(_dir, "Alpha"), Send("Build alpha."));
        var b = await AddIdeaAsync(ideas, "Beta", Path.Combine(_dir, "Beta"), Send("Build beta."));
        await OnlyTheseIdeas(ideas, a, b);
        var w1 = new ScriptedWorker();
        var w2 = new ScriptedWorker();
        using (fleet)
        {
            await fleet.BuildNowAsync(a, w1);
            await Until(() => w1.Received.Count == 1, "Alpha's instruction");
            var auto = await fleet.StartAutoBuilderAsync(w2);
            await Until(() => w2.Received.Count == 1, "the Auto-Builder's instruction");

            Assert.Equal("Beta", auto.CurrentIdea?.Title);
            Assert.StartsWith("Build beta.", w2.Received[0]);
            Assert.Single(w1.Received);
            Assert.Same(auto, fleet.AutoBuilder);
        }
    }

    [Fact]
    public async Task A_build_now_orchestrator_stops_with_its_idea_and_leaves_the_queue_alone()
    {
        var (fleet, ideas) = Setup();
        var a = await AddIdeaAsync(ideas, "Alpha", Path.Combine(_dir, "Alpha"),
            Send("Build alpha."), new OrchestratorDecision(OrchestratorAction.Complete, null, "none", "all done", "done"));
        var b = await AddIdeaAsync(ideas, "Beta", Path.Combine(_dir, "Beta"), Send("Build beta."));
        await OnlyTheseIdeas(ideas, a, b);
        var worker = new ScriptedWorker();
        using (fleet)
        {
            var engine = await fleet.BuildNowAsync(a, worker);
            await Until(() => worker.Received.Count == 1, "Alpha's instruction");
            worker.FinishTurn("Alpha done, tests pass.");
            await Until(() => engine.State == OrchestratorState.Idle, "Alpha's orchestrator to finish");
            await Task.Delay(300);

            Assert.Single(worker.Received); // Beta was not started
            Assert.Equal(IdeaStatus.Queued, (await ideas.LoadAsync()).Single(i => i.Id == b.Id).Status);
            Assert.DoesNotContain(Runs(), r => r.IdeaTitle == "Beta");
            Assert.Same(engine, fleet.EngineFor(a.Id)); // its log and conversation stay readable
            Assert.Empty(fleet.Active);
        }
    }
}
