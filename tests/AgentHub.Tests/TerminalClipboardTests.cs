using System.Runtime.InteropServices;
using System.Text;
using AgentHub.Terminal;
using Xunit;

namespace AgentHub.Tests;

/// <summary>Copying out of a terminal: OSC 52 requests from programs (Claude Code's own selection) and the clipboard write.</summary>
public class TerminalClipboardTests
{
    private static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));

    [Fact]
    public void A_write_request_is_decoded_as_utf8()
    {
        // As Claude Code sent it in a live session (2026-11-13): selector c, base64 of the selected text.
        Assert.Equal("Read the handoff in CLAUDE.md", TerminalClipboard.ParseOsc52("c;" + B64("Read the handoff in CLAUDE.md")));
        Assert.Equal("─ Perth → 12:31 ✓", TerminalClipboard.ParseOsc52("c;" + B64("─ Perth → 12:31 ✓")));
        Assert.Equal("line 1\r\nline 2", TerminalClipboard.ParseOsc52(";" + B64("line 1\r\nline 2"))); // empty selector means the default
        Assert.Equal("p", TerminalClipboard.ParseOsc52("p;" + B64("p")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("c")]          // no data part
    [InlineData("c;")]         // "clear the clipboard": not a copy
    [InlineData("c;?")]        // a read request: never answered
    [InlineData("c;not base64!")]
    public void Anything_but_a_plain_write_is_ignored(string? payload) => Assert.Null(TerminalClipboard.ParseOsc52(payload));

    [Fact]
    public void A_copy_larger_than_the_limit_is_ignored()
    {
        var big = new string('x', TerminalClipboard.MaxChars + 1);
        Assert.Null(TerminalClipboard.ParseOsc52("c;" + B64(big)));
        var fits = new string('x', 1000);
        Assert.Equal(fits, TerminalClipboard.ParseOsc52("c;" + B64(fits)));
    }

    [Fact]
    public async Task A_clipboard_held_by_another_program_is_tried_again()
    {
        var tries = 0;
        string? set = null;
        var ok = await TerminalClipboard.SetTextAsync("hello", t => { if (++tries < 3) throw new COMException("CLIPBRD_E_CANT_OPEN", unchecked((int)0x800401D0)); set = t; }, attempts: 5, delayMs: 1);
        Assert.True(ok);
        Assert.Equal(3, tries);
        Assert.Equal("hello", set);
    }

    [Fact]
    public async Task A_clipboard_that_stays_locked_reports_failure()
    {
        var tries = 0;
        var ok = await TerminalClipboard.SetTextAsync("hello", _ => { tries++; throw new COMException("CLIPBRD_E_CANT_OPEN", unchecked((int)0x800401D0)); }, attempts: 4, delayMs: 1);
        Assert.False(ok);
        Assert.Equal(4, tries);
        Assert.False(await TerminalClipboard.SetTextAsync("", _ => tries++));
        Assert.Equal(4, tries); // nothing to copy: the clipboard is not touched
    }
}
