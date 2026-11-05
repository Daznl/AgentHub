using System.IO;
using AgentHub.Models;
using AgentHub.Services;
using AgentHub.Services.Context;
using AgentHub.Services.Ideas;
using AgentHub.Services.Instructions;
using AgentHub.Services.Orchestrator;
using Xunit;
using Xunit.Abstractions;

namespace AgentHub.Tests;

/// <summary>
/// A real worker without a terminal: each message is one <c>claude -p</c> turn resuming the same session, in the project
/// folder, with file tools and only git/node allowed in Bash. Lets the whole orchestrator loop run for real in a test.
/// </summary>
internal sealed class PrintModeWorker(string model) : IBuildWorker
{
    private readonly ProcessRunner _runner = new();
    private string _dir = "";
    private string _session = "";
    private bool _started;

    public List<string> Received { get; } = [];
    public string Label => "print-mode worker";
    public bool IsRunning { get; private set; }
    public bool IsAwaitingInput { get; private set; }
    public TimeSpan OutputSilentFor => IsAwaitingInput ? TimeSpan.FromHours(1) : TimeSpan.Zero;
    public string? TranscriptPath => null;
    public ContextReading? LatestContextReading => null;
    public string? LastReply { get; private set; }
    public event Action? TurnEnded;
    public event Action? Exited;
    public event Action<ContextReading?>? ContextChanged { add { } remove { } }

    public void Start(string commandLine, string workingDirectory, string title, string sessionName)
    {
        _dir = workingDirectory;
        _session = Guid.NewGuid().ToString();
        _started = false;
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
        var session = _session;
        _ = Task.Run(async () =>
        {
            string[] args =
            [
                "-p", "--model", model, _started ? "--resume" : "--session-id", session, "--output-format", "json",
                "--tools", "Read,Write,Edit,Glob,Grep,Bash",
                "--allowedTools", "Read", "Write", "Edit", "Glob", "Grep", "Bash(git:*)", "Bash(node:*)", "Bash(cd:*)", "Bash(ls:*)", "Bash(cat:*)", "Bash(head:*)", "Bash(mkdir:*)",
                "--permission-mode", "dontAsk", "--setting-sources", "", "--strict-mcp-config",
                "--append-system-prompt", "Create and change files with the Write and Edit tools. Run shell commands one per Bash call (node, git, ls, cat, head, mkdir), without chaining or heredocs; anything else is denied."
            ];
            _started = true;
            var r = await _runner.RunAsync("claude", args, _dir, timeout: TimeSpan.FromMinutes(15), standardInput: prompt,
                removeEnvironment: ClaudeOrchestratorBrain.StrippedEnvironment);
            string reply;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(r.StdOut.Trim());
                reply = doc.RootElement.TryGetProperty("result", out var res) ? res.GetString() ?? "" : r.StdOut;
            }
            catch { reply = $"(worker CLI failed, exit {r.ExitCode}) {ClaudePrint.Trim(r.StdErr + r.StdOut)}"; }
            if (session != _session || !IsRunning) return; // stopped meanwhile
            LastReply = reply;
            IsAwaitingInput = true;
            TurnEnded?.Invoke();
        });
        return Task.CompletedTask;
    }

    public string? ReadLastReply() => LastReply;
    public string? ReadLastPrompt() => null; // print mode: every prompt is passed whole as an argument
}

public class LiveOrchestratorTests(ITestOutputHelper output)
{
    /// <summary>
    /// The whole loop for real: Sonnet orchestrates a Sonnet worker building a tiny Node CLI, Daniel's steer arrives mid-run,
    /// and the finished run is published to a scratch repository's archive branch. Spends Claude usage, so opt-in.
    /// Leaves its folder under C:\data\agenthub\orch-live for reading the REVIEW.md.
    /// </summary>
    [LiveFact("claude", optIn: "AGENTHUB_LIVE_ORCHESTRATOR")]
    public async Task Orchestrator_builds_a_tiny_app_end_to_end_with_a_steer()
    {
        var root = Path.Combine(@"C:\data\agenthub\orch-live", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(root);
        var runner = new ProcessRunner();

        var archiveRepo = Path.Combine(root, "archive repo");
        Directory.CreateDirectory(archiveRepo);
        await runner.RunAsync("git", ["init", "-q", "-b", "main"], archiveRepo);
        await runner.RunAsync("git", ["commit", "-q", "--allow-empty", "-m", "init"], archiveRepo);

        var ideas = new IdeaService(Path.Combine(root, "ideas.json"));
        var idea = new ProjectIdea
        {
            Title = "Greeter CLI",
            Description = "A tiny Node.js command-line tool: `node hello.js --name Ada` prints `Hello, Ada!` (default name World). " +
                          "Include a test file using node:test that `node --test` runs. No dependencies. Keep it tiny; this is a test of the build loop.",
            TargetDirectory = Path.Combine(root, "Greeter CLI"),
            AgentCommand = "claude"
        };
        await ideas.AddIdeaAsync(idea);
        await OrchestratorLoopTests.OnlyThisIdea(ideas, idea);

        var worker = new PrintModeWorker("sonnet");
        var engine = new OrchestratorEngine(ideas, new GitService(runner), runner, new SteeringService(Path.Combine(root, "steering.json")))
        {
            RunsRoot = Path.Combine(root, "runs"),
            Archive = new AgentHubArchive(runner, archiveRepo, Path.Combine(root, "archive worktree")),
            UsageReader = () => Task.FromResult<UsageSnapshot?>(null),
            TurnSettle = TimeSpan.FromSeconds(1),
            WorkerStartupDelay = TimeSpan.Zero,
            LoopInterval = TimeSpan.FromSeconds(1),
            IdleRecheck = TimeSpan.FromMinutes(2),
            MaxDecisionsPerRun = 15
        };
        engine.LogAdded += l => output.WriteLine($"[{l.FormattedTime}] {l.Message}");

        using (engine)
        {
            await engine.BuildIdeaImmediatelyAsync(idea, worker);
            for (var i = 0; i < 600 && worker.Received.Count == 0; i++) await Task.Delay(500);
            Assert.NotEmpty(worker.Received);

            // Daniel's steer while the worker is on its first turn.
            await engine.SteerAsync(idea, "Also add a --shout flag that prints the greeting in upper case.", urgent: false);

            var deadline = DateTime.Now.AddMinutes(25);
            var review = "";
            while (DateTime.Now < deadline)
            {
                review = Directory.Exists(engine.RunsRoot)
                    ? Directory.GetDirectories(engine.RunsRoot).Select(d => Path.Combine(d, "REVIEW.md")).FirstOrDefault(File.Exists) ?? ""
                    : "";
                if (review.Length > 0 || engine.State == OrchestratorState.Paused) break;
                await Task.Delay(2000);
            }
            output.WriteLine("Review: " + review);
            output.WriteLine("Progress: " + engine.LatestProgress);
            if (review.Length == 0) engine.Stop();
            for (var i = 0; i < 60 && review.Length == 0; i++)
            {
                await Task.Delay(1000);
                review = Directory.GetDirectories(engine.RunsRoot).Select(d => Path.Combine(d, "REVIEW.md")).FirstOrDefault(File.Exists) ?? "";
            }
            Assert.True(review.Length > 0, "the run never produced a REVIEW.md");

            var record = RunRecorder.ReadRecord(Path.Combine(Path.GetDirectoryName(review)!, "run.json"))!;
            output.WriteLine($"Outcome {record.Outcome}: {record.OutcomeDetail}; decisions {record.OrchestratorCalls}, sent {record.MessagesToWorker}, steers {record.Steers}");
            Assert.Equal("completed", record.Outcome);
            Assert.Equal(1, record.Steers);
            Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(review)!, "orchestrator.jsonl")), "orchestrator transcript copied");
            Assert.DoesNotContain(worker.Received, m => m.Contains("[STEER FROM DANIEL]"));

            var run = await runner.RunAsync("node", ["hello.js", "--name", "Ada", "--shout"], idea.TargetDirectory);
            output.WriteLine("hello.js --shout: " + run.StdOut.Trim());
            Assert.Contains("HELLO, ADA!", run.StdOut);

            var files = await runner.RunAsync("git", ["ls-tree", "-r", "--name-only", AgentHubArchive.Branch], archiveRepo);
            Assert.Contains($"runs/{record.RunId}/REVIEW.md", files.StdOut);
        }
    }
}
