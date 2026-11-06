using System.Text.RegularExpressions;

namespace AgentHub.Terminal;

/// <summary>
/// Spots Claude Code's "Do you trust this folder?" check on screen and gives the keys that answer yes. Its default answer
/// is "No, exit", so a prompt typed at it followed by Enter ends the session (2026-11-05: every orchestrated build in a new
/// project folder stopped there and paused). Only orchestrator workers answer it on their own: the owner picked the folder
/// for the build. Panes he starts himself still ask him.
/// </summary>
public sealed class FolderTrustPrompt
{
    private static readonly Regex Ansi = new(@"\x1b\[[0-9;?<>=]*[ -/]*[@-~]|\x1b\][^\x07\x1b]*(\x07|\x1b\\)|\x1b[=>()][0-9A-Za-z]?", RegexOptions.Compiled);
    private string _screen = "";

    /// <summary>The check is on screen and not yet answered.</summary>
    public bool Pending { get; private set; }

    /// <summary>When it was answered; null if it never showed.</summary>
    public DateTimeOffset? AnsweredAt { get; private set; }

    public void Reset() { _screen = ""; Pending = false; AnsweredAt = null; }

    /// <summary>Reads terminal output. When the check has just appeared, returns the keys to press before Enter ("" or Down);
    /// otherwise null.</summary>
    public string? Observe(string output, DateTimeOffset now)
    {
        if (AnsweredAt != null || string.IsNullOrEmpty(output)) return null;
        _screen += Ansi.Replace(output, " ");
        if (_screen.Length > 6000) _screen = _screen[^6000..];
        var flat = Regex.Replace(_screen, @"\s+", " ");
        var yes = flat.LastIndexOf("Yes, I trust this folder", StringComparison.OrdinalIgnoreCase);
        if (yes < 0 || flat.IndexOf("Enter to confirm", yes, StringComparison.OrdinalIgnoreCase) < 0) return null;

        Pending = true;
        AnsweredAt = now;
        // ❯ marks the selected option. Already on "Yes": Enter alone. Otherwise (on "No, exit", the default): Down first.
        var no = flat.LastIndexOf("No, exit", StringComparison.OrdinalIgnoreCase);
        var cursor = flat.LastIndexOf('❯', yes);
        var onYes = cursor >= 0 && cursor > no;
        return onYes ? "" : "\x1b[B";
    }

    /// <summary>The answer has been typed: Enter after the Down.</summary>
    public void Answered() => Pending = false;
}
