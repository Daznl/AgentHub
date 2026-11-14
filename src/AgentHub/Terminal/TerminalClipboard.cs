using System.Runtime.InteropServices;
using System.Text;

namespace AgentHub.Terminal;

/// <summary>
/// Copying out of a Cockpit terminal. Two routes reach the host: the page's own selection (Ctrl+C or Ctrl+Shift+C with text
/// selected, right-click Copy), and a program in the terminal asking for a copy with OSC 52 (<c>ESC ] 52 ; c ; base64 BEL</c>).
/// Claude Code uses the second: it turns mouse tracking on, draws its own selection and copies on release, by OSC 52 plus a
/// <c>powershell Set-Clipboard</c> it kills after 2 s, which is shorter than PowerShell takes to start on a managed PC.
/// Before this the page ignored OSC 52 and copied through <c>navigator.clipboard</c>, dropping every failure silently, so
/// copying worked only some of the time (owner feedback 2026-11-13: "very variable").
/// </summary>
public static class TerminalClipboard
{
    /// <summary>Largest copy taken from a program (the text after decoding); a terminal is no way to move more.</summary>
    public const int MaxChars = 4 * 1024 * 1024;

    /// <summary>
    /// The text an OSC 52 payload (<c>Pc;Pd</c>, everything between <c>52;</c> and the terminator) asks to copy, or null for
    /// anything that is not a plain write: a read request (<c>?</c>, never answered: a program must not read the clipboard),
    /// an empty payload (a "clear"), bad base64, or more than <see cref="MaxChars"/>.
    /// </summary>
    public static string? ParseOsc52(string? payload)
    {
        if (string.IsNullOrEmpty(payload)) return null;
        var semi = payload.IndexOf(';');
        if (semi < 0) return null;
        var data = payload[(semi + 1)..].Trim();
        if (data.Length == 0 || data == "?") return null;
        if (data.Length > (MaxChars / 3 + 1) * 4 + 8) return null;
        try
        {
            var text = Encoding.UTF8.GetString(Convert.FromBase64String(data));
            return text.Length == 0 || text.Length > MaxChars ? null : text;
        }
        catch (FormatException) { return null; }
    }

    /// <summary>
    /// Puts text on the Windows clipboard from the UI thread, trying again while another program holds the clipboard open
    /// (clipboard managers, Teams, remote desktop do that for a few milliseconds at a time). False when it never got in.
    /// </summary>
    public static async Task<bool> SetTextAsync(string text, Action<string>? setText = null, int attempts = 8, int delayMs = 40)
    {
        if (string.IsNullOrEmpty(text)) return false;
        setText ??= t => System.Windows.Clipboard.SetDataObject(t, copy: true);
        for (var i = 0; i < attempts; i++)
        {
            try
            {
                setText(text);
                return true;
            }
            catch (ExternalException) // COMException CLIPBRD_E_CANT_OPEN: another program has the clipboard open
            {
                if (i < attempts - 1) await Task.Delay(delayMs);
            }
        }
        return false;
    }
}
