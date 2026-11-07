using System.Text.Json;
using AgentHub.Services.Context;
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
        public ContextReading? LatestContextReading => null;
        public string? LastReply { get; set; }
        public string? LastPrompt { get; set; }
        public List<string> Submitted { get; } = [];
        public event Action? TurnEnded;
        public event Action? Exited;
        public event Action<ContextReading?>? ContextChanged;
        public void Start(string commandLine, string workingDirectory, string title, string sessionName) { }
        public void Stop() { IsRunning = false; Exited?.Invoke(); ContextChanged?.Invoke(null); }
        public Task SubmitAsync(string prompt) { Submitted.Add(prompt); return Task.CompletedTask; }
        public string? ReadLastReply() => LastReply;
        public string? ReadLastPrompt() => LastPrompt;
        public void EndTurn(string reply) { LastReply = reply; TurnEnded?.Invoke(); }
    }

    private sealed class ScriptedBrain : IPaneBrain
    {
        public List<string> Messages { get; } = [];
        public Func<string, PaneDecision> Answer { get; set; } = _ => new PaneDecision(null, null, "");
        public Task<PaneDecision> DecideAsync(string message, CancellationToken cancellationToken)
        {
            Messages.Add(message);
            return Task.FromResult(Answer(message));
        }
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
        var worker = new ScriptedWorker { LastPrompt = "fix the parser", LastReply = "Parser fixed, 12 tests pass." };
        var brain = new ScriptedBrain { Answer = _ => new PaneDecision("On it: asked for tests on the edge cases.", "Add tests for empty and malformed input.", "testing the parser") };
        using var o = new PaneOrchestrator(worker, brain, "Orchestrator") { TurnSettle = TimeSpan.Zero };

        await o.SendAsync("Make sure the parser has edge-case tests before we move on.");

        var message = Assert.Single(brain.Messages);
        Assert.Contains("[FROM OWNER]\nMake sure the parser", message);
        Assert.Contains("[WORKER SO FAR]", message); // the first message says where the session stood
        Assert.Contains("fix the parser", message);
        Assert.Contains("Parser fixed", message);
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
        Assert.DoesNotContain("[WORKER SO FAR]", brain.Messages[1]); // only the first message introduces the session
        Assert.Contains(o.Conversation, x => x.Kind == ExchangeKind.Reply && x.Text == "Done: the tests pass.");

        worker.EndTurn("All 12 tests pass."); // the same turn signalled twice is not asked about twice
        await Task.Delay(150);
        Assert.Equal(2, brain.Messages.Count);
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
    public void Decision_parses_blank_strings_as_nothing()
    {
        using var doc = JsonDocument.Parse("""{"to_owner":"  ","to_worker":" Run the tests. ","note":"n"}""");
        var d = PaneDecision.Parse(doc.RootElement);
        Assert.Null(d.ToOwner);
        Assert.Equal("Run the tests.", d.ToWorker);
        Assert.Equal("n", d.Note);
        using var schema = JsonDocument.Parse(PaneDecision.Schema);
        Assert.Equal(["to_owner", "to_worker", "note"], schema.RootElement.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
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
    public void Attached_box_has_a_message_line_and_no_pause()
    {
        var worker = new ScriptedWorker();
        using var o = new PaneOrchestrator(worker, new ScriptedBrain(), "Reviewer");
        var view = OrchestratorBoxView.ForAttached(o);
        Assert.True(view.Attached);
        Assert.Equal("LISTENING", view.State);
        Assert.Equal("🧭 Reviewer", view.Title);
        Assert.False(view.Paused);
    }
}
