using System.IO;
using System.Text.Json;
using AgentHub.Models;
using AgentHub.Services;
using AgentHub.Services.Context;
using AgentHub.Services.Ideas;
using AgentHub.Services.Orchestrator;
using Xunit;

namespace AgentHub.Tests;

public class OrchestratorDecisionTests
{
    private static OrchestratorDecision Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return OrchestratorDecision.Parse(doc.RootElement);
    }

    [Fact]
    public void Send_carries_the_message_for_the_worker()
    {
        var d = Parse("""{"action":"send","to_worker":" Add tests for the parser. ","steer_handling":"relayed","reason":"r","progress":"p"}""");
        Assert.Equal(OrchestratorAction.Send, d.Action);
        Assert.Equal("Add tests for the parser.", d.ToWorker);
        Assert.Equal("relayed", d.SteerHandling);
    }

    [Fact]
    public void Only_send_keeps_a_message_for_the_worker()
    {
        var d = Parse("""{"action":"wait","to_worker":"ignored","steer_handling":"none","reason":"training","progress":"p"}""");
        Assert.Equal(OrchestratorAction.Wait, d.Action);
        Assert.Null(d.ToWorker);
        Assert.Equal(OrchestratorAction.PauseForDaniel, Parse("""{"action":"pause_for_daniel","to_worker":null,"steer_handling":"none","reason":"r","progress":"Pay for the API?"}""").Action);
    }

    [Theory]
    [InlineData("""{"action":"send","to_worker":"","steer_handling":"none","reason":"r","progress":"p"}""")]
    [InlineData("""{"action":"build it","to_worker":null,"steer_handling":"none","reason":"r","progress":"p"}""")]
    [InlineData("""[1,2]""")]
    public void Unusable_decisions_are_refused_with_a_reason(string json) =>
        Assert.Throws<FormatException>(() => Parse(json));

    [Fact]
    public void Schema_is_valid_json_and_names_every_action()
    {
        using var doc = JsonDocument.Parse(OrchestratorDecision.Schema);
        var actions = doc.RootElement.GetProperty("properties").GetProperty("action").GetProperty("enum").EnumerateArray().Select(e => e.GetString()).ToList();
        foreach (var a in Enum.GetValues<OrchestratorAction>()) Assert.Contains(OrchestratorDecision.ActionName(a), actions);
    }

    [Fact]
    public void Orchestrator_runs_read_only_on_the_project_and_resumes_its_own_session()
    {
        var first = ClaudeOrchestratorBrain.Arguments("sonnet", "abc", resume: false, @"C:\p\My App", "sys");
        var later = ClaudeOrchestratorBrain.Arguments("sonnet", "abc", resume: true, @"C:\p\My App", "sys");

        Assert.Equal("--session-id", first[first.ToList().IndexOf("abc") - 1]);
        Assert.Equal("--resume", later[later.ToList().IndexOf("abc") - 1]);
        Assert.Equal("Read,Grep,Glob", first[first.ToList().IndexOf("--tools") + 1]);
        Assert.Equal("dontAsk", first[first.ToList().IndexOf("--permission-mode") + 1]);
        Assert.Equal(@"C:\p\My App", first[first.ToList().IndexOf("--add-dir") + 1]);
        Assert.DoesNotContain(first, a => a.Contains("Bash") || a.Contains("Edit") || a.Contains("Write"));
        Assert.Contains("ANTHROPIC_API_KEY", ClaudeOrchestratorBrain.StrippedEnvironment);
    }
}

public class OrchestratorMessagesTests
{
    [Fact]
    public void Turn_end_message_labels_every_source_so_steers_are_never_mistaken_for_the_worker()
    {
        var msg = OrchestratorMessages.Compose(new OrchestratorInput(OrchestratorEvent.WorkerTurnEnded)
        {
            WorkerReply = "Stage 1 done, 31 tests pass.",
            Steers = [new SteeringNote { Text = "Use pytest", CreatedAt = new DateTimeOffset(2026, 11, 6, 9, 5, 0, TimeSpan.Zero) }],
            Status = "worker session 1 · decision 3",
            RecentCommits = ["abc123 Stage 1"]
        });

        Assert.StartsWith("[EVENT] worker_turn_ended", msg);
        Assert.Contains("[STATUS] worker session 1 · decision 3", msg);
        Assert.Contains("[RECENT COMMITS] newest first\nabc123 Stage 1", msg);
        Assert.Contains("[WORKER FINAL REPLY]\nStage 1 done, 31 tests pass.", msg);
        Assert.Contains("[STEER FROM DANIEL]\n1. (09:05) Use pytest", msg);
        Assert.DoesNotContain("URGENT", msg);
        Assert.EndsWith("Reply with the decision JSON.", msg);
    }

    [Fact]
    public void Urgent_steer_is_marked_and_run_start_carries_the_spec_and_handoff()
    {
        var urgent = OrchestratorMessages.Compose(new OrchestratorInput(OrchestratorEvent.SteerReceived)
        { Steers = [new SteeringNote { Text = "Stop training" }], UrgentSteer = true, WorkerBusy = true });
        Assert.Contains("[STEER FROM DANIEL] URGENT: relay now", urgent);
        Assert.Contains("mid-turn", urgent);
        Assert.DoesNotContain("[WORKER FINAL REPLY]", urgent);

        var start = OrchestratorMessages.Compose(new OrchestratorInput(OrchestratorEvent.RunStarted) { Spec = "Build a bot", Handoff = "# Notes" });
        Assert.Contains("[SPEC]\nBuild a bot", start);
        Assert.Contains("[HANDOFF.MD]\n# Notes", start);
    }

    [Fact]
    public void Long_replies_keep_their_ending_where_the_summary_is()
    {
        var reply = new string('a', 20000) + "FINAL SUMMARY";
        var cut = OrchestratorMessages.Cut(reply, 8000, keepTail: true);
        Assert.True(cut.Length < 8100);
        Assert.EndsWith("FINAL SUMMARY", cut);
        Assert.Contains("middle cut", cut);
    }

    [Fact]
    public void Worker_rules_and_system_prompt_say_who_is_who()
    {
        var sys = OrchestratorMessages.SystemPrompt("Bot", @"C:\x");
        Assert.Contains("Daniel is not in this conversation", sys);
        Assert.Contains("The worker only sees \"to_worker\"", sys);
        Assert.Contains("orchestrator, not a person", OrchestratorMessages.AutonomyRules);
    }
}

public class RunArchiveTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "AgentHub Run Tests " + Guid.NewGuid().ToString("n")[..8]);

    public RunArchiveTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_dir, recursive: true);
        }
        catch { }
    }

    private static RunRecord Record(string id, string title) => new()
    {
        RunId = id, IdeaTitle = title, Spec = "Build it", TargetDirectory = @"C:\p", WorkerCommand = "claude", OrchestratorModel = "sonnet",
        OrchestratorSessionId = "sess-1", StartedAt = new DateTimeOffset(2026, 11, 6, 9, 0, 0, TimeSpan.Zero)
    };

    [Fact]
    public async Task Recorder_writes_live_and_finish_adds_the_review_and_the_orchestrator_transcript()
    {
        var projects = Path.Combine(_dir, "claude-projects", "C--data-run");
        Directory.CreateDirectory(projects);
        await File.WriteAllTextAsync(Path.Combine(projects, "sess-1.jsonl"), "{\"type\":\"user\"}\n");

        var rec = new RunRecorder(Path.Combine(_dir, "run1"), Record("run1", "Stock Bot"));
        await rec.AddAsync(new RunEvent { At = DateTimeOffset.Now, Kind = "turn", Trigger = "worker turn ended", WorkerReply = "Did X", Action = "send", ToWorker = "Now do Y", Reason = "X verified" });
        await rec.AddAsync(new RunEvent { At = DateTimeOffset.Now, Kind = "forced_handoff", Detail = "context 76%", Flag = true });
        Assert.True(File.Exists(rec.RecordPath));
        Assert.Equal(1, RunRecorder.ReadRecord(rec.RecordPath)!.Flags);

        rec.Record.WorkerCommits = ["abc1234 Stage 1"];
        await rec.FinishAsync("completed", "All done", Path.Combine(_dir, "claude-projects"));

        var review = await File.ReadAllTextAsync(Path.Combine(rec.Folder, "REVIEW.md"));
        Assert.Contains("# Orchestrator run: Stock Bot", review);
        Assert.Contains("**completed** · All done", review);
        Assert.Contains("worker turn ended → **send**", review);
        Assert.Contains("> Now do Y", review);
        Assert.Contains("## ⚠ Flagged moments", review);
        Assert.Contains("`abc1234` Stage 1", review);
        Assert.True(File.Exists(Path.Combine(rec.Folder, "orchestrator.jsonl")));
        Assert.Equal("completed", RunRecorder.ReadRecord(rec.RecordPath)!.Outcome);
        Assert.Equal("end", RunRecorder.ReadEvents(rec.EventsPath)[^1].Kind);
    }

    [Fact]
    public void Index_lists_newest_first_with_links_to_each_review()
    {
        var older = Record("r-old", "Old | idea");
        var newer = Record("r-new", "New idea");
        newer.StartedAt = older.StartedAt.AddDays(1);
        var index = RunReview.RenderIndex([older, newer]);
        Assert.True(index.IndexOf("r-new/REVIEW.md", StringComparison.Ordinal) < index.IndexOf("r-old/REVIEW.md", StringComparison.Ordinal));
        Assert.Contains("Old \\| idea", index);
    }

    [Fact]
    public void Run_ids_are_readable_and_safe_as_folder_names()
    {
        var id = RunRecorder.NewRunId("AI Stock Bot: v2!", new DateTimeOffset(2026, 11, 6, 9, 5, 0, TimeSpan.Zero));
        Assert.StartsWith("2026-11-06_0905_ai-stock-bot-v2_", id);
        Assert.DoesNotContain(id, c => Path.GetInvalidFileNameChars().Contains(c));
    }

    private async Task<(int, string)> Git(string dir, params string[] args)
    {
        var r = await new ProcessRunner().RunAsync("git", args, dir, timeout: TimeSpan.FromSeconds(60));
        return (r.ExitCode, r.StdOut.Trim() + r.StdErr.Trim());
    }

    [Fact]
    public async Task Publisher_commits_runs_to_their_own_branch_without_touching_the_working_branch()
    {
        // Repository with a space in its path, on a feature branch with an uncommitted change.
        var repo = Path.Combine(_dir, "Agent Hub repo");
        Directory.CreateDirectory(repo);
        await Git(repo, "init", "-q", "-b", "main");
        await Git(repo, "config", "user.email", "t@example.com");
        await Git(repo, "config", "user.name", "Test");
        await File.WriteAllTextAsync(Path.Combine(repo, "app.txt"), "v1");
        await Git(repo, "add", ".");
        await Git(repo, "commit", "-q", "-m", "init");
        await Git(repo, "switch", "-q", "-c", "feature/x");
        await File.WriteAllTextAsync(Path.Combine(repo, "app.txt"), "work in progress");

        var runs = Path.Combine(_dir, "runs");
        foreach (var (id, title) in new[] { ("2026-11-06_0900_bot_aaaaaa", "Bot"), ("2026-11-06_1000_app_bbbbbb", "App") })
        {
            var rec = new RunRecorder(Path.Combine(runs, id), Record(id, title));
            await rec.AddAsync(new RunEvent { At = DateTimeOffset.Now, Kind = "note", Detail = "x" });
            await rec.FinishAsync("completed", null, Path.Combine(_dir, "no-projects"));
            await File.WriteAllTextAsync(Path.Combine(rec.Folder, "worker-transcript.jsonl"), "secret");
        }

        var publisher = new AgentHubArchive(new ProcessRunner(), repo, Path.Combine(_dir, "archive worktree"));
        var first = await publisher.PublishRunAsync(Path.Combine(runs, "2026-11-06_0900_bot_aaaaaa"));
        var second = await publisher.PublishRunAsync(Path.Combine(runs, "2026-11-06_1000_app_bbbbbb"));
        Assert.Contains("not pushed", first);
        Assert.Contains("not pushed", second);

        Assert.Equal((0, "feature/x"), await Git(repo, "rev-parse", "--abbrev-ref", "HEAD"));
        Assert.Equal("work in progress", await File.ReadAllTextAsync(Path.Combine(repo, "app.txt")));
        Assert.False(Directory.Exists(Path.Combine(repo, AgentHubArchive.RunsFolder)));

        var (_, files) = await Git(repo, "ls-tree", "-r", "--name-only", AgentHubArchive.Branch);
        Assert.Contains("README.md", files);
        Assert.Contains("runs/INDEX.md", files);
        Assert.Contains("runs/2026-11-06_0900_bot_aaaaaa/REVIEW.md", files);
        Assert.Contains("runs/2026-11-06_1000_app_bbbbbb/run.json", files);
        Assert.DoesNotContain("worker-transcript", files);

        var (_, log) = await Git(repo, "log", "--format=%s", AgentHubArchive.Branch);
        Assert.Contains("Orchestrator run 2026-11-06_1000_app_bbbbbb: App (completed)", log);
        var (_, mainLog) = await Git(repo, "log", "--format=%s", "main");
        Assert.Equal("init", mainLog);

        var index = await File.ReadAllTextAsync(Path.Combine(publisher.RunsPath, "INDEX.md"));
        Assert.Contains("[Bot](2026-11-06_0900_bot_aaaaaa/REVIEW.md)", index);
        Assert.Contains("[App](2026-11-06_1000_app_bbbbbb/REVIEW.md)", index);
    }
}

/// <summary>A worker whose turns the test plays out.</summary>
internal sealed class ScriptedWorker : IBuildWorker
{
    public List<string> Received { get; } = [];
    public string? Reply { get; set; }
    public int Starts { get; private set; }
    public string Label => "Test worker";
    public bool IsRunning { get; private set; }
    public bool IsAwaitingInput { get; private set; }
    public TimeSpan OutputSilentFor => IsAwaitingInput ? TimeSpan.FromHours(1) : TimeSpan.Zero;
    public string? TranscriptPath => null;
    public ContextReading? LatestContextReading { get; set; }
    public event Action? TurnEnded;
    public event Action? Exited;
    public event Action<ContextReading?>? ContextChanged;

    /// <summary>The folder the last session started in.</summary>
    public string? StartedIn { get; private set; }

    public void Start(string commandLine, string workingDirectory, string title, string sessionName)
    {
        StartedIn = workingDirectory;
        Starts++;
        IsRunning = true;
        IsAwaitingInput = true;
    }

    public void Stop()
    {
        IsRunning = false;
        Exited?.Invoke();
    }

    public Task SubmitAsync(string prompt)
    {
        lock (Received) Received.Add(prompt);
        IsAwaitingInput = false;
        return Task.CompletedTask;
    }

    public string? ReadLastReply() => Reply;

    /// <summary>What the transcript shows for a received prompt; set to simulate a message cut on the way in.</summary>
    public Func<string, string>? Arrives { get; set; }

    public string? ReadLastPrompt() { lock (Received) return Received.Count == 0 ? null : (Arrives ?? (p => p))(Received[^1]); }

    /// <summary>The turn end the transcript shows; set without <see cref="FinishTurn"/> to simulate one the terminal missed.</summary>
    public DateTimeOffset? TurnEndedAt { get; set; }

    public DateTimeOffset? ReadTurnEndedAt() => TurnEndedAt;

    public void FinishTurn(string reply)
    {
        Reply = reply;
        IsAwaitingInput = true;
        TurnEnded?.Invoke();
    }

    public void ReportContext(ContextReading reading)
    {
        LatestContextReading = reading;
        ContextChanged?.Invoke(reading);
    }
}

/// <summary>An orchestrator that answers from a queue and keeps every message it was sent.</summary>
internal sealed class ScriptedBrain : IOrchestratorBrain
{
    private readonly Queue<OrchestratorDecision> _answers;
    public List<string> Messages { get; } = [];
    public string SessionId => "scripted";

    public ScriptedBrain(params OrchestratorDecision[] answers) => _answers = new Queue<OrchestratorDecision>(answers);

    public Task<BrainReply> DecideAsync(string message, CancellationToken cancellationToken)
    {
        lock (Messages) Messages.Add(message);
        var d = _answers.Count > 0 ? _answers.Dequeue() : new OrchestratorDecision(OrchestratorAction.Wait, null, "none", "script ran out", "");
        return Task.FromResult(new BrainReply(d, 1, TimeSpan.FromMilliseconds(5)));
    }
}

public class OrchestratorLoopTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "AgentHub Loop " + Guid.NewGuid().ToString("n")[..8]);

    public OrchestratorLoopTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_dir, recursive: true);
        }
        catch { }
    }

    /// <summary>IdeaService seeds a default idea pointing at a real Desktop folder; a test queue must hold only its own idea.</summary>
    internal static async Task OnlyThisIdea(IdeaService ideas, ProjectIdea idea)
    {
        foreach (var other in (await ideas.LoadAsync()).Where(i => i.Id != idea.Id)) await ideas.DeleteIdeaAsync(other.Id);
        Assert.Single(await ideas.LoadAsync());
    }

    private static OrchestratorDecision Send(string text, string steer = "none") => new(OrchestratorAction.Send, text, steer, "next step", "building");

    private (OrchestratorEngine Engine, IdeaService Ideas, ProjectIdea Idea) Setup(ScriptedBrain brain)
    {
        var runner = new ProcessRunner();
        var ideas = new IdeaService(Path.Combine(_dir, "ideas.json"));
        var idea = new ProjectIdea { Title = "Tiny App", Description = "Print hello", TargetDirectory = Path.Combine(_dir, "Tiny App"), ContextHandoffThreshold = 0.75 };
        var engine = new OrchestratorEngine(ideas, new GitService(runner), runner, new SteeringService(Path.Combine(_dir, "steering.json")))
        {
            RunsRoot = Path.Combine(_dir, "runs"),
            BrainFactory = (_, _) => brain,
            UsageReader = () => Task.FromResult<UsageSnapshot?>(null),
            TurnSettle = TimeSpan.Zero,
            WorkerStartupDelay = TimeSpan.Zero,
            LoopInterval = TimeSpan.FromMilliseconds(50),
            IdleRecheck = TimeSpan.FromHours(1)
        };
        return (engine, ideas, idea);
    }

    private static async Task Until(Func<bool> condition, string what)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(25);
        Assert.True(condition(), "Timed out waiting for: " + what);
    }

    /// <summary>A run ends in the background: wait until its REVIEW.md is written.</summary>
    private Task UntilReviewed() => Until(() =>
    {
        var runs = Path.Combine(_dir, "runs");
        return Directory.Exists(runs) && Directory.GetDirectories(runs).Any(d => File.Exists(Path.Combine(d, "REVIEW.md")));
    }, "the run's REVIEW.md");

    [Fact]
    public async Task Orchestrator_drives_the_worker_turn_by_turn_and_holds_steers_until_the_turn_ends()
    {
        var brain = new ScriptedBrain(
            Send("Scaffold the project."),
            Send("Add a test for hello.", steer: "relayed"),
            new OrchestratorDecision(OrchestratorAction.Complete, null, "none", "Spec met and tested", "done"));
        var (engine, ideas, idea) = Setup(brain);
        await ideas.AddIdeaAsync(idea);
        await OrchestratorLoopTests.OnlyThisIdea(ideas, idea);
        var worker = new ScriptedWorker();
        using (engine)
        {
            await engine.BuildIdeaImmediatelyAsync(idea, worker);

            // Run start: the orchestrator saw the spec; the worker got its instruction plus AgentHub's rules.
            await Until(() => worker.Received.Count == 1, "first instruction");
            Assert.StartsWith("[EVENT] run_started", brain.Messages[0]);
            Assert.Contains("[SPEC]\nPrint hello", brain.Messages[0]);
            Assert.StartsWith("Scaffold the project. ", worker.Received[0]);
            Assert.Contains(OrchestratorMessages.AutonomyRules, worker.Received[0]);

            // A steer while the worker is busy waits for its turn end and goes to the orchestrator, not the worker.
            await engine.SteerAsync(idea, "Use pytest", urgent: false);
            await Task.Delay(150);
            Assert.Single(brain.Messages);
            Assert.Single(worker.Received);

            worker.FinishTurn("Scaffolded, 1 file.");
            await Until(() => worker.Received.Count == 2, "second instruction");
            Assert.Contains("[WORKER FINAL REPLY]\nScaffolded, 1 file.", brain.Messages[1]);
            Assert.Contains("Use pytest", brain.Messages[1]);
            Assert.Equal("Add a test for hello.", worker.Received[1]);
            Assert.DoesNotContain(worker.Received, m => m.Contains("Use pytest"));

            worker.FinishTurn("Test added and passing.");
            await UntilReviewed();
        }

        Assert.Equal(IdeaStatus.Completed, (await ideas.LoadAsync()).Single(i => i.Id == idea.Id).Status);
        var runFolder = Directory.GetDirectories(Path.Combine(_dir, "runs")).Single();
        var record = RunRecorder.ReadRecord(Path.Combine(runFolder, "run.json"))!;
        Assert.Equal("completed", record.Outcome);
        Assert.Equal(3, record.OrchestratorCalls);
        Assert.Equal(2, record.MessagesToWorker);
        Assert.Equal(1, record.Steers);
        var review = await File.ReadAllTextAsync(Path.Combine(runFolder, "REVIEW.md"));
        Assert.Contains("> Use pytest", review);
        Assert.Contains("_Handling: relayed_", review);
        Assert.Contains("→ **complete**", review);
        Assert.True((await engine.Steering.GetPendingAsync(idea.Id)).Count == 0);
    }

    [Fact]
    public async Task Full_context_forces_a_handoff_and_the_orchestrator_briefs_the_fresh_worker()
    {
        var brain = new ScriptedBrain(Send("Build it."), Send("Read the handoff and carry on."));
        var (engine, ideas, idea) = Setup(brain);
        await ideas.AddIdeaAsync(idea);
        await OrchestratorLoopTests.OnlyThisIdea(ideas, idea);
        var worker = new ScriptedWorker();
        using (engine)
        {
            await engine.BuildIdeaImmediatelyAsync(idea, worker);
            await Until(() => worker.Received.Count == 1, "first instruction");

            worker.ReportContext(new ContextReading(50_000, 1_000_000, false, "opus", DateTimeOffset.Now.AddSeconds(1), "test", "x")); // a fresh session
            worker.ReportContext(new ContextReading(800_000, 1_000_000, false, "opus", DateTimeOffset.Now.AddSeconds(1), "test", "x"));
            await Until(() => worker.Received.Count == 2, "handoff prompt");
            Assert.Equal(OrchestratorMessages.HandoffPrompt, worker.Received[1]);
            Assert.Equal(OrchestratorState.HandoffPending, engine.State);

            worker.FinishTurn("Handoff written.");
            await Until(() => worker.Received.Count == 3, "fresh session's first instruction");
            Assert.Equal(2, worker.Starts);
            Assert.StartsWith("[EVENT] new_worker_session", brain.Messages[1]);
            Assert.StartsWith("Read the handoff and carry on. ", worker.Received[2]);
            Assert.Equal(1, engine.CurrentRun!.Handoffs);
            Assert.Equal(2, brain.Messages.Count); // the handoff turn itself was not sent to the orchestrator
            engine.Stop();
            await UntilReviewed();
        }

        var runFolder = Directory.GetDirectories(Path.Combine(_dir, "runs")).Single();
        var record = RunRecorder.ReadRecord(Path.Combine(runFolder, "run.json"))!;
        Assert.Equal("stopped", record.Outcome);
        Assert.Equal(2, record.WorkerSessions.Count);
        Assert.Equal("handoff", record.WorkerSessions[0].EndReason);
        Assert.Contains(RunRecorder.ReadEvents(Path.Combine(runFolder, "events.jsonl")), e => e.Kind == "forced_handoff" && e.Flag);
    }

    /// <summary>2026-11-05 live test with a 60k window: a fresh Claude Code session starts at 77% (46k tokens), over the 75%
    /// limit, so handing off on that reading would start session after session.</summary>
    [Fact]
    public async Task A_worker_that_starts_over_the_context_limit_is_not_handed_off_in_a_loop()
    {
        var brain = new ScriptedBrain(Send("Build it."));
        var (engine, ideas, idea) = Setup(brain);
        await ideas.AddIdeaAsync(idea);
        await OrchestratorLoopTests.OnlyThisIdea(ideas, idea);
        var worker = new ScriptedWorker();
        using (engine)
        {
            await engine.BuildIdeaImmediatelyAsync(idea, worker);
            await Until(() => worker.Received.Count == 1, "first instruction");

            worker.ReportContext(new ContextReading(46_286, 60_000, true, "opus", DateTimeOffset.Now.AddSeconds(1), "test", "x"));
            worker.ReportContext(new ContextReading(50_000, 60_000, true, "opus", DateTimeOffset.Now.AddSeconds(1), "test", "x"));
            await Task.Delay(300);
            Assert.Single(worker.Received);
            Assert.Equal(OrchestratorState.Building, engine.State);

            worker.ReportContext(new ContextReading(54_000, 60_000, true, "opus", DateTimeOffset.Now.AddSeconds(1), "test", "x")); // 90%: past halfway to full
            await Until(() => worker.Received.Count == 2, "handoff prompt");
            Assert.Equal(OrchestratorMessages.HandoffPrompt, worker.Received[1]);
            engine.Stop();
            await UntilReviewed();
        }
    }

    /// <summary>2026-11-05 live test: the handoff request was queued during a busy turn and ran as the next one; the terminal
    /// never signalled its end, so the build sat waiting. The transcript's turn end now counts.</summary>
    [Fact]
    public async Task A_turn_end_only_the_transcript_shows_still_completes_the_handoff()
    {
        var brain = new ScriptedBrain(Send("Build it."), Send("Read the handoff and carry on."));
        var (engine, ideas, idea) = Setup(brain);
        await ideas.AddIdeaAsync(idea);
        await OrchestratorLoopTests.OnlyThisIdea(ideas, idea);
        var worker = new ScriptedWorker();
        using (engine)
        {
            await engine.BuildIdeaImmediatelyAsync(idea, worker);
            await Until(() => worker.Received.Count == 1, "first instruction");
            worker.TurnEndedAt = DateTimeOffset.Now.AddMinutes(-5); // the start-up turn, before anything was sent: not ours
            worker.ReportContext(new ContextReading(50_000, 1_000_000, false, "opus", DateTimeOffset.Now.AddSeconds(1), "test", "x"));
            worker.ReportContext(new ContextReading(800_000, 1_000_000, false, "opus", DateTimeOffset.Now.AddSeconds(1), "test", "x"));
            await Until(() => worker.Received.Count == 2, "handoff prompt");
            await Task.Delay(300);
            Assert.Equal(1, worker.Starts); // an old turn end is not the handoff turn's

            worker.TurnEndedAt = DateTimeOffset.Now; // the handoff turn ends; the terminal says nothing
            await Until(() => worker.Received.Count == 3, "fresh session's first instruction");
            Assert.Equal(2, worker.Starts);
            Assert.StartsWith("Read the handoff and carry on. ", worker.Received[2]);
            engine.Stop();
            await UntilReviewed();
        }
    }

    [Fact]
    public void The_transcripts_last_turn_end_counts_only_when_no_prompt_follows_it()
    {
        const string end = """{"type":"system","subtype":"turn_duration","durationMs":93000,"timestamp":"2026-11-05T08:44:16.000Z"}""";
        const string prompt = """{"type":"user","message":{"role":"user","content":"[AGENTHUB] Time to hand off"},"timestamp":"2026-11-05T08:42:43.500Z"}""";
        const string toolResult = """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"x","content":"ok"}]}}""";
        const string queued = """{"type":"queue-operation","operation":"enqueue","timestamp":"2026-11-05T08:42:43.009Z","content":"[AGENTHUB] Time to hand off"}""";
        const string reply = """{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"Done."}]}}""";

        Assert.Equal(DateTimeOffset.Parse("2026-11-05T08:44:16Z"), TranscriptTurns.LastTurnEnd(["{\"cut", prompt, toolResult, reply, end, """{"type":"last-prompt"}"""]));
        Assert.Null(TranscriptTurns.LastTurnEnd([end, queued, toolResult]));   // queued during the turn: another turn follows
        Assert.Null(TranscriptTurns.LastTurnEnd([end, prompt, toolResult, reply])); // running
        Assert.Null(TranscriptTurns.LastTurnEnd([prompt]));
    }

    private static (ExchangeKind, string)[] Talk(OrchestratorEngine engine) => engine.Conversation.Select(x => (x.Kind, x.Text)).ToArray();

    /// <summary>The orchestrator box lists every reply read and message sent in the worker session, waits included.</summary>
    [Fact]
    public async Task The_engine_keeps_the_worker_sessions_whole_conversation()
    {
        var brain = new ScriptedBrain(Send("Build slice 1."), Send("Build slice 2."),
            new OrchestratorDecision(OrchestratorAction.Wait, null, "none", "tests still running", "waiting"));
        var (engine, ideas, idea) = Setup(brain);
        await ideas.AddIdeaAsync(idea);
        await OrchestratorLoopTests.OnlyThisIdea(ideas, idea);
        var worker = new ScriptedWorker();
        using (engine)
        {
            await engine.BuildIdeaImmediatelyAsync(idea, worker);
            await Until(() => worker.Received.Count == 1, "first instruction");
            Assert.Equal([(ExchangeKind.Sent, "Build slice 1.")], Talk(engine)); // without AgentHub's rules

            worker.FinishTurn("Slice 1 done, 5 tests pass.");
            await Until(() => worker.Received.Count == 2, "second instruction");
            worker.FinishTurn("Still running the slow tests.");
            await Until(() => brain.Messages.Count == 3 && !engine.IsThinking, "the wait decision");
            Assert.Equal(
            [
                (ExchangeKind.Sent, "Build slice 1."),
                (ExchangeKind.Read, "Slice 1 done, 5 tests pass."),
                (ExchangeKind.Sent, "Build slice 2."),
                (ExchangeKind.Read, "Still running the slow tests."),
                (ExchangeKind.Wait, "tests still running"),
            ], Talk(engine));
            engine.Stop();
            await UntilReviewed();
        }
    }

    /// <summary>Daniel 2026-11-06: the box shows only the current worker session; a fresh worker starts it over.</summary>
    [Fact]
    public async Task A_fresh_worker_starts_the_conversation_over()
    {
        var brain = new ScriptedBrain(Send("Build it."),
            new OrchestratorDecision(OrchestratorAction.Handoff, null, "none", "long session", "handing off"),
            Send("Read the handoff and carry on."));
        var (engine, ideas, idea) = Setup(brain);
        await ideas.AddIdeaAsync(idea);
        await OrchestratorLoopTests.OnlyThisIdea(ideas, idea);
        var worker = new ScriptedWorker();
        using (engine)
        {
            await engine.BuildIdeaImmediatelyAsync(idea, worker);
            await Until(() => worker.Received.Count == 1, "first instruction");
            worker.FinishTurn("Half of it is built.");
            await Until(() => worker.Received.Count == 2, "handoff prompt");
            Assert.Equal(
            [
                (ExchangeKind.Sent, "Build it."),
                (ExchangeKind.Read, "Half of it is built."),
                (ExchangeKind.AgentHub, OrchestratorMessages.HandoffPrompt),
            ], Talk(engine));

            worker.FinishTurn("Handoff written.");
            await Until(() => worker.Received.Count == 3, "fresh session's first instruction");
            Assert.Equal(2, worker.Starts);
            Assert.Equal([(ExchangeKind.Sent, "Read the handoff and carry on.")], Talk(engine));
            engine.Stop();
            await UntilReviewed();
        }
    }

    [Fact]
    public async Task A_question_for_Daniel_pauses_the_build_and_resuming_asks_the_orchestrator_again()
    {
        var brain = new ScriptedBrain(
            Send("Start."),
            new OrchestratorDecision(OrchestratorAction.PauseForDaniel, null, "none", "Needs a paid key", "Should I buy a market data subscription?"),
            Send("Use the free data source.", steer: "relayed"));
        var (engine, ideas, idea) = Setup(brain);
        await ideas.AddIdeaAsync(idea);
        await OrchestratorLoopTests.OnlyThisIdea(ideas, idea);
        var worker = new ScriptedWorker();
        using (engine)
        {
            await engine.BuildIdeaImmediatelyAsync(idea, worker);
            await Until(() => worker.Received.Count == 1, "first instruction");
            worker.FinishTurn("Which data source should I use? The good one costs money.");
            await Until(() => engine.State == OrchestratorState.Paused, "pause");
            Assert.Equal("Waiting for you: Should I buy a market data subscription?", engine.LatestProgress);
            Assert.Equal(IdeaStatus.Paused, (await ideas.LoadAsync()).Single(i => i.Id == idea.Id).Status);

            await engine.SteerAsync(idea, "No paid services.", urgent: false);
            await engine.ResumeAsync();
            await Until(() => worker.Received.Count == 2, "instruction after resume");
            Assert.StartsWith("[EVENT] resumed", brain.Messages[2]);
            Assert.Contains("No paid services.", brain.Messages[2]);
            engine.Stop();
            await UntilReviewed();
        }
    }

    [Fact]
    public async Task Closing_the_builders_pane_pauses_the_build_and_starts_nothing_until_Daniel_resumes()
    {
        // Daniel 2026-11-05: closing the builder's pane got three fresh workers in a minute ("it keeps running sessions").
        var brain = new ScriptedBrain(Send("Build slice 1."), Send("Carry on from the handoff."));
        var (engine, ideas, idea) = Setup(brain);
        await ideas.AddIdeaAsync(idea);
        await OrchestratorLoopTests.OnlyThisIdea(ideas, idea);
        var worker = new ScriptedWorker();
        using (engine)
        {
            await engine.BuildIdeaImmediatelyAsync(idea, worker);
            await Until(() => worker.Received.Count == 1, "first instruction");

            worker.Stop(); // the pane closed under it, not by AgentHub
            await Until(() => engine.State == OrchestratorState.Paused, "paused");
            await Task.Delay(500);
            Assert.Equal(1, worker.Starts);
            Assert.Single(brain.Messages);
            Assert.Equal(IdeaStatus.Paused, (await ideas.LoadAsync()).Single(i => i.Id == idea.Id).Status);

            await engine.ResumeAsync();
            await Until(() => worker.Received.Count == 2, "fresh worker after Resume");
            Assert.Equal(2, worker.Starts);
            engine.Stop();
            await UntilReviewed();
        }
    }

    [Fact]
    public async Task Writing_the_spec_after_a_question_and_pressing_Build_Now_hands_the_new_spec_to_the_orchestrator()
    {
        // Daniel 2026-11-05: Build Now on the placeholder spec, the orchestrator asked "What should New App #1 be?", he wrote it.
        var brain = new ScriptedBrain(
            new OrchestratorDecision(OrchestratorAction.PauseForDaniel, null, "none", "Spec is only the placeholder", "What should New App #1 be?"),
            Send("Build the habit tracker: start with the data model."));
        var (engine, ideas, idea) = Setup(brain);
        idea.Description = "Describe the goal, architecture, features, and requirements for this application.";
        await ideas.AddIdeaAsync(idea);
        await OrchestratorLoopTests.OnlyThisIdea(ideas, idea);
        var worker = new ScriptedWorker();
        using (engine)
        {
            await engine.BuildIdeaImmediatelyAsync(idea, worker);
            await Until(() => engine.State == OrchestratorState.Paused, "question for Daniel");

            var updated = (await ideas.LoadAsync()).Single(i => i.Id == idea.Id);
            updated.Description = "A habit tracker: daily habits, streaks, a weekly chart.";
            await ideas.UpdateIdeaAsync(updated);
            await engine.BuildIdeaImmediatelyAsync(updated, worker);
            await Until(() => worker.Received.Count == 1, "instruction after the spec was written");

            // The question came before any worker started, so this is a fresh worker session; either way the new spec is in the message.
            Assert.DoesNotContain("Describe the goal", brain.Messages[1]);
            Assert.Contains("[SPEC]\nA habit tracker: daily habits, streaks, a weekly chart.", brain.Messages[1]);
            engine.Stop();
            await UntilReviewed();
        }
    }
}
