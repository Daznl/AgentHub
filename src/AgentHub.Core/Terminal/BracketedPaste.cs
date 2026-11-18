namespace AgentHub.Terminal;

/// <summary>
/// Tracks whether the program in a terminal has turned on bracketed paste (ESC[?2004h, off with ESC[?2004l), and wraps
/// text as one paste when it has. Claude Code reads a long raw burst in 1024-byte pieces and, with two pieces, kept only
/// the second (2026-11-05: an 1100-character orchestrator message reached the worker as its last 76 characters).
/// Marked as a paste, the whole text arrives as one.
/// </summary>
public sealed class BracketedPaste
{
    public const string On = "\x1b[?2004h";
    public const string Off = "\x1b[?2004l";
    public const string Start = "\x1b[200~";
    public const string End = "\x1b[201~";

    private string _tail = "";

    public bool Enabled { get; private set; }

    /// <summary>Reads terminal output; the last mode switch seen wins. Copes with a switch split across two chunks.</summary>
    public void Observe(string output)
    {
        if (string.IsNullOrEmpty(output)) return;
        var text = _tail + output;
        var on = text.LastIndexOf(On, StringComparison.Ordinal);
        var off = text.LastIndexOf(Off, StringComparison.Ordinal);
        if (on >= 0 || off >= 0) Enabled = on > off;
        _tail = text.Length > On.Length ? text[^(On.Length - 1)..] : text;
    }

    public void Reset() { Enabled = false; _tail = ""; }

    /// <summary>The bytes to type for <paramref name="text"/>: one bracketed paste when enabled, else as is. Escape characters
    /// are dropped from the text so it cannot end the paste early.</summary>
    public string Wrap(string text)
    {
        var clean = text.Replace("\x1b", "");
        return Enabled ? Start + clean + End : clean;
    }
}
