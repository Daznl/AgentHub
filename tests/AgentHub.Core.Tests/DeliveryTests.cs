using System.IO;
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
}

/// <summary>2026-11-05: every orchestrated session in a new project folder stopped at Claude Code's folder trust check, whose
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
        Assert.False(t.CheckOnScreen);
    }

    /// <summary>2026-11-07: the owner answers security checks; while one is on screen nothing is typed into the session.</summary>
    [Fact]
    public void A_trust_check_the_owner_answers_counts_as_on_screen_until_they_type()
    {
        var t = new FolderTrustPrompt();
        Assert.Equal("\x1b[B", t.Observe(Screen, DateTimeOffset.Now)); // the keys are only used for a worker; the owner's pane ignores them
        Assert.True(t.CheckOnScreen);
        t.UserTyped();
        Assert.False(t.CheckOnScreen);
        Assert.False(t.Pending);
    }

    [Fact]
    public void A_permission_dialog_is_a_check_on_screen_until_the_owner_types()
    {
        var t = new FolderTrustPrompt();
        Assert.Null(t.Observe("\x1b[1mBash command\x1b[0m\r\n  git commit -m \"x\"\r\n\r\n Do you want to proceed?\r\n\x1b[36m❯\x1b[39m 1. Yes\r\n  2. No, and tell Claude what to do differently (esc)", DateTimeOffset.Now));
        Assert.True(t.CheckOnScreen);
        t.UserTyped();
        Assert.False(t.CheckOnScreen);
        // Redrawn after a key that did not answer it: on screen again.
        t.Observe(" Do you want to proceed?\r\n  1. Yes\r\n\x1b[36m❯\x1b[39m 2. No, and tell Claude what to do differently (esc)", DateTimeOffset.Now);
        Assert.True(t.CheckOnScreen);
    }
}
