using System.Text.RegularExpressions;

namespace AgentHub.Terminal;

/// <summary>
/// Spots Claude Code's security checks on screen: the "Do you trust this folder?" check, and the permission dialogs
/// ("Do you want to proceed?", "Do you want to make this edit?"). The trust check's default answer is "No, exit", so a
/// prompt typed at it followed by Enter ends the session (2026-11-05: every orchestrated build in a new project folder
/// stopped there and paused); a permission dialog's Enter means "Yes". Only orchestrator workers answer the trust check on
/// their own (the owner picked the folder for the build); every other check is the owner's to answer (owner decision
/// 2026-11-07: "make it so the user has to do the security checks"), and while one is on screen
/// (<see cref="CheckOnScreen"/>) nothing may be typed into the session on the owner's behalf.
/// </summary>
public sealed class FolderTrustPrompt
{
    private static readonly Regex Ansi = new(@"\x1b\[[0-9;?<>=]*[ -/]*[@-~]|\x1b\][^\x07\x1b]*(\x07|\x1b\\)|\x1b[=>()][0-9A-Za-z]?", RegexOptions.Compiled);
    private static readonly string[] PermissionMarkers =
        ["Do you want to proceed?", "Do you want to make this edit", "Do you want to create", "tell Claude what to do differently"];
    private string _screen = "";

    /// <summary>The trust check is on screen and not yet answered.</summary>
    public bool Pending { get; private set; }

    /// <summary>When the trust check was answered (or seen, when the owner answers it); null if it never showed.</summary>
    public DateTimeOffset? AnsweredAt { get; private set; }

    /// <summary>A security check (trust or permission) is on screen waiting for the owner's keys.</summary>
    public bool CheckOnScreen { get; private set; }

    public void Reset() { _screen = ""; Pending = false; AnsweredAt = null; CheckOnScreen = false; }

    /// <summary>Reads terminal output. When the trust check has just appeared, returns the keys to press before Enter ("" or Down);
    /// otherwise null. Sets <see cref="CheckOnScreen"/> for any check it sees.</summary>
    public string? Observe(string output, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(output)) return null;
        _screen += Ansi.Replace(output, " ");
        if (_screen.Length > 6000) _screen = _screen[^6000..];
        var flat = Regex.Replace(_screen, @"\s+", " ");

        if (PermissionMarkers.Any(m => flat.Contains(m, StringComparison.OrdinalIgnoreCase))) CheckOnScreen = true;

        if (AnsweredAt != null) return null;
        var yes = flat.LastIndexOf("Yes, I trust this folder", StringComparison.OrdinalIgnoreCase);
        if (yes < 0 || flat.IndexOf("Enter to confirm", yes, StringComparison.OrdinalIgnoreCase) < 0) return null;

        Pending = true;
        CheckOnScreen = true;
        AnsweredAt = now;
        // ❯ marks the selected option. Already on "Yes": Enter alone. Otherwise (on "No, exit", the default): Down first.
        var no = flat.LastIndexOf("No, exit", StringComparison.OrdinalIgnoreCase);
        var cursor = flat.LastIndexOf('❯', yes);
        var onYes = cursor >= 0 && cursor > no;
        return onYes ? "" : "\x1b[B";
    }

    /// <summary>The answer has been typed by AgentHub: Enter after the Down.</summary>
    public void Answered() { Pending = false; CheckOnScreen = false; _screen = ""; }

    /// <summary>
    /// The owner typed into the terminal: whatever check was on screen is taken as answered. If it is still there, the CLI
    /// redraws it and the next output shows it again.
    /// </summary>
    public void UserTyped() { Pending = false; CheckOnScreen = false; _screen = ""; }
}
