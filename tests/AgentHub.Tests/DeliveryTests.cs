using System.IO;
using AgentHub.Services;
using AgentHub.Services.Ideas;
using AgentHub.Services.Orchestrator;
using AgentHub.Models;
using AgentHub.Terminal;
using Xunit;

namespace AgentHub.Tests;

/// <summary>
/// 2026-11-05: an 1100-character orchestrator message reached the worker as its last 76 characters. Claude Code read the raw
/// burst in 1024-byte pieces and kept only the second. Messages now go as one bracketed paste, and each is checked against
/// the worker's transcript.
/// </summary>
public class DeliveryTests
{
    [Fact]
    public void Bracketed_paste_follows_the_programs_last_mode_switch_even_when_split_across_chunks()
    {
        var p = new BracketedPaste();
        Assert.False(p.Enabled);
        p.Observe("welcome \x1b[?20");
        p.Observe("04h prompt");
        Assert.True(p.Enabled);
        p.Observe("bye\x1b[?2004l");
        Assert.False(p.Enabled);
        p.Observe("\x1b[?2004l...\x1b[?2004h");
        Assert.True(p.Enabled);
        p.Reset();
        Assert.False(p.Enabled);
    }

    [Fact]
    public void A_prompt_is_one_bracketed_paste_when_enabled_and_cannot_end_it_early()
    {
        var p = new BracketedPaste();
        Assert.Equal("plain text", p.Wrap("plain text"));
        p.Observe("\x1b[?2004h");
        Assert.Equal("\x1b[200~a b\x1b[201~", p.Wrap("a b"));
        Assert.Equal("\x1b[200~x[201~ y\x1b[201~", p.Wrap("x\x1b[201~ y"));
    }

    private static readonly string Long = string.Join(" ", Enumerable.Range(1, 120).Select(i => $"word{i}")) + " Report the commands you ran.";

    [Fact]
    public void Delivery_is_whole_inside_Claude_Codes_pasted_content_wrapper_and_cut_when_only_the_end_arrived()
    {
        Assert.Equal("whole", OrchestratorEngine.DeliveryOf(Long, "\n\n<pasted_content id=\"18bd\">\n" + Long + "\n</pasted_content>"));
        Assert.Equal("cut", OrchestratorEngine.DeliveryOf(Long, Long[^76..]));
        Assert.Null(OrchestratorEngine.DeliveryOf(Long, "an older prompt"));
        Assert.Null(OrchestratorEngine.DeliveryOf(Long, null));
    }
}

public class DeliveryLoopTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "AgentHub Delivery " + Guid.NewGuid().ToString("n")[..8]);

    public DeliveryLoopTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_dir, recursive: true);
        }
        catch { }
    }

    [Fact]
    public async Task A_message_that_reached_the_worker_cut_off_is_flagged_and_the_orchestrator_is_told()
    {
        var first = "Build slice 1. " + string.Join(" ", Enumerable.Range(1, 150).Select(i => $"point{i}")) + " Commit when the tests pass.";
        var brain = new ScriptedBrain(
            new OrchestratorDecision(OrchestratorAction.Send, first, "none", "start", "working"),
            new OrchestratorDecision(OrchestratorAction.Send, "Here is what was missing.", "none", "resend", "working"));
        var runner = new ProcessRunner();
        var ideas = new IdeaService(Path.Combine(_dir, "ideas.json"));
        var idea = new ProjectIdea { Title = "Delivery", Description = "d", TargetDirectory = Path.Combine(_dir, "Delivery app") };
        await ideas.AddIdeaAsync(idea);
        await OrchestratorLoopTests.OnlyThisIdea(ideas, idea);
        var runs = Path.Combine(_dir, "runs");
        using var engine = new OrchestratorEngine(ideas, new GitService(runner), runner, new SteeringService(Path.Combine(_dir, "steering.json")))
        {
            RunsRoot = runs,
            BrainFactory = (_, _) => brain,
            UsageReader = () => Task.FromResult<UsageSnapshot?>(null),
            TurnSettle = TimeSpan.Zero,
            WorkerStartupDelay = TimeSpan.Zero,
            LoopInterval = TimeSpan.FromMilliseconds(50),
            IdleRecheck = TimeSpan.FromHours(1),
            DeliveryCheckTimeout = TimeSpan.FromSeconds(2)
        };
        var worker = new ScriptedWorker { Arrives = p => p[^76..] }; // only the end arrives, as on 2026-11-05
        await engine.BuildIdeaImmediatelyAsync(idea, worker);
        for (var i = 0; i < 100 && worker.Received.Count == 0; i++) await Task.Delay(25);
        await Task.Delay(600); // the delivery check runs

        worker.Arrives = null;
        worker.FinishTurn("I only got the end of your message.");
        for (var i = 0; i < 200 && brain.Messages.Count < 2; i++) await Task.Delay(25);

        Assert.StartsWith("[EVENT] worker_turn_ended", brain.Messages[1]);
        Assert.Contains("[DELIVERY] Your previous message reached the worker cut off. It received only: \"", brain.Messages[1]);
        var events = RunRecorder.ReadEvents(Path.Combine(Directory.GetDirectories(runs).Single(), "events.jsonl"));
        Assert.Contains(events, e => e.Kind == "delivery_cut" && e.Flag);
        engine.Stop();
    }
}

/// <summary>2026-11-05: every orchestrated build in a new project folder stopped at Claude Code's folder trust check, whose
/// default is "No, exit"; the first prompt and its Enter answered it and the worker exited.</summary>
public class FolderTrustPromptTests
{
    // As Claude Code 2.1.318 drew it, with its colours and cursor moves.
    private const string Screen =
        "\x1b[?2004h\x1b[1mAccessing workspace:\x1b[0m C:\\data\\agenthub\\pastetest\r\n Quick safety check: Is this a project you created or one you trust?" +
        " \x1b]8;id=u;https://code.claude.com/docs/en/security\x1b\\Security guide\x1b]8;;\x1b\\\r\n\x1b[36m❯\x1b[39m 1. No, exit\r\n  2. Yes, I trust this folder\r\n\r\n Enter to confirm · Esc to cancel";

    [Fact]
    public void On_the_default_No_it_presses_Down_before_Enter_and_answers_only_once()
    {
        var t = new FolderTrustPrompt();
        var now = DateTimeOffset.Now;
        Assert.Null(t.Observe(Screen[..120], now));
        Assert.Equal("\x1b[B", t.Observe(Screen[120..], now));
        Assert.True(t.Pending);
        t.Answered();
        Assert.False(t.Pending);
        Assert.Equal(now, t.AnsweredAt);
        Assert.Null(t.Observe(Screen, now));
    }

    [Fact]
    public void Already_on_Yes_it_presses_Enter_alone()
    {
        var onYes = Screen.Replace("\x1b[36m❯\x1b[39m 1. No, exit\r\n  2.", "  1. No, exit\r\n\x1b[36m❯\x1b[39m 2.");
        Assert.Equal("", new FolderTrustPrompt().Observe(onYes, DateTimeOffset.Now));
    }

    [Fact]
    public void An_ordinary_screen_is_not_the_trust_check()
    {
        var t = new FolderTrustPrompt();
        Assert.Null(t.Observe("Claude Code v2.1.318 ❯ Try \"create a util logging.py that...\" Yes, I trust my tests", DateTimeOffset.Now));
        Assert.Null(t.AnsweredAt);
    }
}
