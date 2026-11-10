using System.IO;
using System.Text.Json;
using AgentHub.Services.Context;
using AgentHub.Services.History;
using AgentHub.Services.Orchestrator;
using Xunit;

namespace AgentHub.Tests;

/// <summary>
/// The pane orchestrator: put on a running session, it gets every owner message and every worker turn end, and answers the
/// owner and/or instructs the worker. These tests drive it with a scripted worker and a scripted brain.
/// </summary>
public class PaneOrchestratorTests
{
    private sealed class ScriptedWorker : IBuildWorker
    {
        public string Label => "Terminal 1";
        public bool IsRunning { get; set; } = true;
        public bool IsAwaitingInput { get; set; } = true;
        public TimeSpan OutputSilentFor { get; set; } = TimeSpan.FromMinutes(1);
        public string? TranscriptPath => null;
        public string? LastReply { get; set; }
        public List<HistoryEntry> History { get; set; } = [];
        public List<string> Submitted { get; } = [];
        public List<string> Started { get; } = [];
        public int Stops { get; private set; }
        public ContextReading? LatestContextReading { get; private set; }
        public SessionLaunch? SessionLaunch { get; set; }
        public bool IsAtSecurityCheck { get; set; }
        public event Action? TurnEnded;
        public event Action? Exited;
        public event Action<ContextReading?>? ContextChanged;
        public void Start(string commandLine, string workingDirectory, string title, string sessionName) { Started.Add($"{commandLine} in {workingDirectory} as {sessionName}"); IsRunning = true; }
        public void Stop() { Stops++; IsRunning = false; Exited?.Invoke(); }
        public void Context(double used, long window = 100_000)
        {
            LatestContextReading = new ContextReading((long)(used * window), window, WindowIsAssumed: true, null, DateTimeOffset.Now, "test", "");
            ContextChanged?.Invoke(LatestContextReading);
        }
        public Task SubmitAsync(string prompt) { Submitted.Add(prompt); return Task.CompletedTask; }
        public string? ReadLastReply() => LastReply;
        public IReadOnlyList<HistoryEntry> ReadHistory() => History;
        public void EndTurn(string reply) { LastReply = reply; TurnEnded?.Invoke(); }
    }

    private sealed class ScriptedBrain : IPaneBrain
    {
        public List<string> Messages { get; } = [];
        public Func<string, PaneDecision> Answer { get; set; } = _ => new PaneDecision(null, null, "");
        /// <summary>What the brain's own transcript says it used; null for a brain without one.</summary>
        public ContextReading? Context { get; set; }
        public Task<PaneDecision> DecideAsync(string message, CancellationToken cancellationToken)
        {
            Messages.Add(message);
            return Task.FromResult(Answer(message));
        }
        public ContextReading? ReadContext() => Context;
    }

    private static async Task Until(Func<bool> done, int ms = 3000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (!done() && DateTime.UtcNow < until) await Task.Delay(20);
        Assert.True(done());
    }

    [Fact]
    public async Task Owner_message_goes_to_the_orchestrator_which_answers_and_instructs_the_worker()
    {
        var worker = new ScriptedWorker
        {
            History =
            [
                new(HistoryKind.Prompt, "build a CSV importer", null),
                new(HistoryKind.Reply, "Importer built.", null),
                new(HistoryKind.Prompt, "fix the parser", null),
                new(HistoryKind.Reply, "Parser fixed, 12 tests pass.", null),
            ],
            LastReply = "Parser fixed, 12 tests pass.",
        };
        var brain = new ScriptedBrain { Answer = _ => new PaneDecision("On it: asked for tests on the edge cases.", "Add tests for empty and malformed input.", "testing the parser") };
        using var o = new PaneOrchestrator(worker, brain, "Orchestrator") { TurnSettle = TimeSpan.Zero };

        await o.SendAsync("Make sure the parser has edge-case tests before we move on.");

        var message = Assert.Single(brain.Messages);
        Assert.Contains("[FROM OWNER]\nMake sure the parser", message);
        Assert.Contains("[CONVERSATION HISTORY]", message); // the first message carries the session from its start
        Assert.Contains("--- Message 1 to the worker ---\nbuild a CSV importer", message);
        Assert.Contains("Importer built.", message);
        Assert.Contains("--- Message 2 to the worker ---\nfix the parser", message);
        Assert.Contains("Parser fixed", message);
        Assert.True(message.IndexOf("[CONVERSATION HISTORY]") < message.IndexOf("[FROM OWNER]")); // background first, then the new request
        Assert.Contains("worker idle", message);
        Assert.Equal(["Add tests for empty and malformed input."], worker.Submitted);
        Assert.Equal("testing the parser", o.Note);
        Assert.Equal([ExchangeKind.Steer, ExchangeKind.Reply, ExchangeKind.Sent], o.Conversation.Select(x => x.Kind));
        Assert.Equal("Orchestrator → you", o.Conversation[1].Label);
    }

    [Fact]
    public async Task Worker_turn_end_is_shown_to_the_orchestrator_once_and_the_owner_is_told()
    {
        var worker = new ScriptedWorker();
        var brain = new ScriptedBrain { Answer = m => m.Contains("[WORKER FINAL REPLY]") ? new PaneDecision("Done: the tests pass.", null, "done") : new PaneDecision(null, "go", "") };
        using var o = new PaneOrchestrator(worker, brain, "Orchestrator") { TurnSettle = TimeSpan.Zero };
        await o.SendAsync("run the tests");
        Assert.Equal(["go"], worker.Submitted);

        worker.EndTurn("All 12 tests pass.");
        await Until(() => brain.Messages.Count == 2);
        Assert.Contains("worker_turn_ended", brain.Messages[1]);
        Assert.Contains("All 12 tests pass.", brain.Messages[1]);
        Assert.DoesNotContain("[CONVERSATION HISTORY]", brain.Messages[1]); // only the first message introduces the session
        Assert.Contains(o.Conversation, x => x.Kind == ExchangeKind.Reply && x.Text == "Done: the tests pass.");

        worker.EndTurn("All 12 tests pass."); // the same turn signalled twice is not asked about twice
        await Task.Delay(150);
        Assert.Equal(2, brain.Messages.Count);

        // After the orchestrator's next message the terminal goes quiet mid-turn (a long tool run): the old reply is still the
        // transcript's last one, so it is not a turn end either (live run 2026-11-07: a duplicate report reached the orchestrator).
        brain.Answer = _ => new PaneDecision(null, "now step 2", "");
        await o.SendAsync("carry on");
        Assert.Equal(3, brain.Messages.Count);
        worker.EndTurn("All 12 tests pass.");
        await Task.Delay(150);
        Assert.Equal(3, brain.Messages.Count);
        worker.EndTurn("Step 2 done.");
        await Until(() => brain.Messages.Count == 4);
    }

    [Fact]
    public async Task Nothing_to_say_and_nothing_to_send_is_a_wait()
    {
        var worker = new ScriptedWorker { IsAwaitingInput = false };
        var brain = new ScriptedBrain { Answer = _ => new PaneDecision(null, null, "worker is mid-task") };
        using var o = new PaneOrchestrator(worker, brain, "Orchestrator");

        await o.SendAsync("how is it going?");

        Assert.Contains("mid-turn", brain.Messages[0]);
        Assert.Empty(worker.Submitted);
        Assert.Equal(ExchangeKind.Wait, o.Conversation.Last().Kind);
    }

    [Fact]
    public async Task A_failed_decision_is_shown_in_the_box_and_the_orchestrator_stays_attached()
    {
        var worker = new ScriptedWorker();
        var brain = new ScriptedBrain { Answer = _ => throw new OrchestratorBrainException("no usable reply") };
        using var o = new PaneOrchestrator(worker, brain, "Orchestrator");

        await o.SendAsync("hello");

        Assert.Contains(o.Conversation, x => x.Kind == ExchangeKind.Note && x.Text.Contains("no usable reply"));
        Assert.False(o.IsThinking);
        Assert.Equal(0, o.Decisions);
    }

    [Fact]
    public async Task Detached_orchestrator_is_not_asked_again()
    {
        var worker = new ScriptedWorker();
        var brain = new ScriptedBrain();
        var o = new PaneOrchestrator(worker, brain, "Orchestrator") { TurnSettle = TimeSpan.Zero };
        o.Dispose();

        worker.EndTurn("done");
        await Task.Delay(100);
        Assert.Empty(brain.Messages);
    }

    [Fact]
    public async Task Full_context_window_hands_off_and_a_fresh_session_is_briefed_from_the_handoff()
    {
        var dir = Path.Combine(Path.GetTempPath(), "agenthub-pane-handoff-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, ".agenthub"));
        await File.WriteAllTextAsync(Path.Combine(dir, ".agenthub", "handoff.md"), "# Notes\nParser done; tests next.");
        try
        {
            var worker = new ScriptedWorker { SessionLaunch = new SessionLaunch("claude --x", dir, "Pane Test", "Pane Test") };
            var brain = new ScriptedBrain { Answer = m => m.Contains("new_worker_session") ? new PaneDecision("Fresh session briefed.", "Read .agenthub/handoff.md, then write the tests.", "session 2") : new PaneDecision(null, "go", "") };
            using var o = new PaneOrchestrator(worker, brain, "Orchestrator") { TurnSettle = TimeSpan.Zero, StopDelay = TimeSpan.Zero, StartupDelay = TimeSpan.Zero };
            await o.SendAsync("build it");

            worker.Context(0.30); // the session's starting size
            Assert.False(o.IsHandingOff);
            worker.Context(0.80); // over the 75% limit
            await Until(() => o.IsHandingOff);
            Assert.Equal(OrchestratorMessages.HandoffPrompt, worker.Submitted.Last());
            Assert.Equal("HANDING OFF", OrchestratorBoxView.For(o).State);

            // The owner can still talk to it, but nothing is typed into a session that is writing its notes.
            brain.Answer = m => m.Contains("new_worker_session") ? new PaneDecision("Fresh session briefed.", "Read .agenthub/handoff.md, then write the tests.", "session 2") : new PaneDecision("Handing off now.", "also do X", "");
            await o.SendAsync("status?");
            Assert.Equal(OrchestratorMessages.HandoffPrompt, worker.Submitted.Last());
            Assert.Contains(o.Conversation, x => x.Kind == ExchangeKind.Note && x.Text.StartsWith("Held:"));

            worker.EndTurn("Handoff written and committed.");
            await Until(() => o.Session == 2 && worker.Submitted.Last().StartsWith("Read .agenthub/handoff.md"));
            Assert.Equal(1, worker.Stops);
            Assert.Equal([$"claude --x in {dir} as Pane Test"], worker.Started);
            var briefing = brain.Messages.Last();
            Assert.Contains("new_worker_session", briefing);
            Assert.Contains("[HANDOFF.MD]\n# Notes\nParser done; tests next.", briefing);
            Assert.DoesNotContain("[WORKER FINAL REPLY]", briefing);
            Assert.False(o.IsHandingOff);
            Assert.Contains("session #2", OrchestratorBoxView.For(o).Title);

            // The new session's own first reading is its baseline, not a reason to hand off again.
            worker.Context(0.80);
            await Task.Delay(100);
            Assert.False(o.IsHandingOff);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task The_orchestrator_can_start_a_fresh_session_early_but_not_again_before_it_has_worked()
    {
        var dir = Path.Combine(Path.GetTempPath(), "agenthub-pane-new-session-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, ".agenthub"));
        await File.WriteAllTextAsync(Path.Combine(dir, ".agenthub", "handoff.md"), "# Notes\nTask 1 done.");
        try
        {
            var worker = new ScriptedWorker { SessionLaunch = new SessionLaunch("claude --x", dir, "Pane Test", "Pane Test") };
            // Asked for a fresh session per task: after task 1 it asks for one, with a to_worker that must not reach the old session.
            var brain = new ScriptedBrain
            {
                Answer = m => m.Contains("new_worker_session")
                    ? new PaneDecision(null, "Read .agenthub/handoff.md, then do task 2.", "task 2", NewSession: "again, straight away")
                    : m.Contains("[WORKER FINAL REPLY]")
                    ? new PaneDecision("Task 1 done; fresh session for task 2 as you asked.", "start task 2", "task 1 done", NewSession: "owner wants a fresh session per task")
                    : new PaneDecision(null, "do task 1", ""),
            };
            using var o = new PaneOrchestrator(worker, brain, "Orchestrator") { TurnSettle = TimeSpan.Zero, StopDelay = TimeSpan.Zero, StartupDelay = TimeSpan.Zero };
            await o.SendAsync("Fresh session for each task, please. Start with task 1.");
            Assert.Equal(["do task 1"], worker.Submitted);

            worker.EndTurn("Task 1 done.");
            await Until(() => o.IsHandingOff);
            Assert.Equal(["do task 1", OrchestratorMessages.HandoffPrompt], worker.Submitted);
            Assert.Contains(o.Conversation, x => x.Kind == ExchangeKind.Note && x.Text.Contains("early: owner wants a fresh session per task"));

            worker.EndTurn("Handoff written.");
            await Until(() => o.Session == 2 && worker.Submitted.Last().StartsWith("Read .agenthub/handoff.md"));
            // The fresh session's briefing asked for yet another session: refused, its instruction delivered instead.
            Assert.Contains(o.Conversation, x => x.Kind == ExchangeKind.Note && x.Text.Contains("session #2 has not finished any work yet"));
            Assert.False(o.IsHandingOff);
            Assert.Equal(1, worker.Stops);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task A_fresh_session_request_is_refused_when_the_launch_is_unknown()
    {
        var worker = new ScriptedWorker();
        var brain = new ScriptedBrain { Answer = _ => new PaneDecision("Starting fresh.", "go", "", NewSession: "owner asked") };
        using var o = new PaneOrchestrator(worker, brain, "Orchestrator") { TurnSettle = TimeSpan.Zero };
        await o.SendAsync("start a fresh session");
        Assert.False(o.IsHandingOff);
        Assert.Equal(["go"], worker.Submitted);
        Assert.Contains(o.Conversation, x => x.Kind == ExchangeKind.Note && x.Text.Contains("cannot start it again"));
    }

    [Fact]
    public async Task Lowering_the_handoff_limit_below_the_current_context_hands_off_at_once()
    {
        var worker = new ScriptedWorker { SessionLaunch = new SessionLaunch("claude --x", "C:\\x", "Pane Test", "Pane Test") };
        var brain = new ScriptedBrain { Answer = _ => new PaneDecision(null, "go", "") };
        using var o = new PaneOrchestrator(worker, brain, "Orchestrator") { TurnSettle = TimeSpan.Zero };
        await o.SendAsync("build it");
        Assert.Equal(0.75, o.ContextThreshold);
        Assert.Equal(0.75, OrchestratorBoxView.For(o).HandoffThreshold);

        worker.Context(0.20); // baseline
        worker.Context(0.55); // under 75%
        Assert.False(o.IsHandingOff);
        Assert.Equal("55% of 100k", OrchestratorBoxView.For(o).ContextUsed);

        o.ContextThreshold = 0.50; // the owner lowers the limit in the box: checked against the latest reading, no new reading needed
        await Until(() => o.IsHandingOff);
        Assert.Equal(OrchestratorMessages.HandoffPrompt, worker.Submitted.Last());
        Assert.Contains(o.Conversation, x => x.Kind == ExchangeKind.Note && x.Text == "Handoff limit set to 50% of the context window.");
        Assert.Contains(o.Conversation, x => x.Kind == ExchangeKind.Note && x.Text.Contains("limit 50%"));
    }

    [Fact]
    public async Task The_orchestrators_own_context_is_read_after_each_decision_for_its_bar()
    {
        var worker = new ScriptedWorker();
        var brain = new ScriptedBrain { Answer = _ => new PaneDecision("ok", null, "") };
        using var o = new PaneOrchestrator(worker, brain, "Orchestrator") { TurnSettle = TimeSpan.Zero };
        Assert.Null(o.OrchestratorContext);
        Assert.Null(OrchestratorBoxView.For(o).OrchestratorContext);

        brain.Context = new ContextReading(12_000, 200_000, WindowIsAssumed: true, "sonnet", DateTimeOffset.Now, "test", "brain.jsonl");
        await o.SendAsync("hello");
        Assert.Equal(12_000, o.OrchestratorContext!.UsedTokens);
        Assert.Equal(0.06, OrchestratorBoxView.For(o).OrchestratorContext!.UsedFraction, 3);

        brain.Context = null; // a read that fails keeps the last reading rather than blanking the bar
        await o.SendAsync("again");
        Assert.Equal(12_000, o.OrchestratorContext!.UsedTokens);
    }

    [Fact]
    public void The_handoff_limit_is_clamped_and_a_workflow_sets_it_without_a_note()
    {
        var worker = new ScriptedWorker();
        using var o = new PaneOrchestrator(worker, new ScriptedBrain(), "Orchestrator", new OrchestratorWorkflow { Name = "w", ContextThreshold = 0.60 });
        Assert.Equal(0.60, o.ContextThreshold);
        Assert.Empty(o.Conversation);

        o.ContextThreshold = 0.10;
        Assert.Equal(PaneOrchestrator.MinContextThreshold, o.ContextThreshold);
        o.ContextThreshold = 1.5;
        Assert.Equal(PaneOrchestrator.MaxContextThreshold, o.ContextThreshold);
        Assert.Equal(2, o.Conversation.Count(x => x.Kind == ExchangeKind.Note && x.Text.StartsWith("Handoff limit set to")));
    }

    [Fact]
    public async Task Nothing_is_typed_into_a_security_check_the_message_waits_for_the_owner()
    {
        var worker = new ScriptedWorker { IsAtSecurityCheck = true };
        var brain = new ScriptedBrain { Answer = _ => new PaneDecision("Sent step 1.", "Create todo.py.", "step 1") };
        using var o = new PaneOrchestrator(worker, brain, "Orchestrator") { LoopInterval = TimeSpan.FromMilliseconds(30), TurnSettle = TimeSpan.Zero };

        await o.SendAsync("build it");
        Assert.Empty(worker.Submitted);
        Assert.Equal("Create todo.py.", o.HeldForWorker);
        Assert.Equal("WAITING FOR YOU", OrchestratorBoxView.For(o).State);
        Assert.Contains(o.Conversation, x => x.Kind == ExchangeKind.Note && x.Text.StartsWith("Held: the session is showing a security check"));

        worker.EndTurn("ignored: a dialog is not a turn end");
        await Task.Delay(100);
        Assert.Single(brain.Messages);

        worker.IsAtSecurityCheck = false; // the owner answered it
        await Until(() => worker.Submitted.Count == 1);
        Assert.Equal("Create todo.py.", worker.Submitted[0]);
        Assert.Null(o.HeldForWorker);
        Assert.Equal("ATTACHED", OrchestratorBoxView.For(o).State);
    }

    [Fact]
    public async Task A_session_without_a_known_launch_never_hands_off()
    {
        var worker = new ScriptedWorker();
        using var o = new PaneOrchestrator(worker, new ScriptedBrain(), "Orchestrator");
        worker.Context(0.30);
        worker.Context(0.90);
        await Task.Delay(100);
        Assert.False(o.IsHandingOff);
        Assert.Empty(worker.Submitted);
    }

    [Fact]
    public void Decision_parses_blank_strings_as_nothing()
    {
        using var doc = JsonDocument.Parse("""{"to_owner":"  ","to_worker":" Run the tests. ","note":"n","new_session":" "}""");
        var d = PaneDecision.Parse(doc.RootElement);
        Assert.Null(d.ToOwner);
        Assert.Equal("Run the tests.", d.ToWorker);
        Assert.Equal("n", d.Note);
        Assert.Null(d.NewSession);
        using var fresh = JsonDocument.Parse("""{"to_owner":null,"to_worker":null,"note":"n","new_session":" task done "}""");
        Assert.Equal("task done", PaneDecision.Parse(fresh.RootElement).NewSession);
        using var schema = JsonDocument.Parse(PaneDecision.Schema);
        Assert.Equal(["to_owner", "to_worker", "note", "new_session"], schema.RootElement.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public void System_prompt_names_the_folder_and_adds_a_workflow_as_context()
    {
        var plain = PaneOrchestrator.SystemPrompt(@"C:\p\My App", "Terminal 2", null);
        Assert.Contains(@"C:\p\My App", plain);
        Assert.DoesNotContain("CONTEXT:", plain);

        var w = OrchestratorWorkflow.Parse("---\nname: Data Processing\ndescription: Runs a pipeline.\ninput.pipeline: C:\\pipe\n---\n# Orchestrator instructions\nWork through {{pipeline}}.\n# Worker rules\nnone", "dp");
        var with = PaneOrchestrator.SystemPrompt(@"C:\p\My App", "Terminal 2", w);
        Assert.Contains("CONTEXT: Data Processing", with);
        Assert.Contains(@"Work through C:\pipe.", with);
    }

    [Fact]
    public void A_fresh_box_listens_and_carries_the_handoff_limit()
    {
        var worker = new ScriptedWorker();
        using var o = new PaneOrchestrator(worker, new ScriptedBrain(), "Reviewer");
        var view = OrchestratorBoxView.For(o);
        Assert.Equal("LISTENING", view.State);
        Assert.Equal("🧭 Reviewer", view.Title);
        Assert.Equal(0.75, view.HandoffThreshold);
        Assert.Null(view.ContextUsed);
    }
}
