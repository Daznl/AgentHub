namespace AgentHub.Models;

/// <summary>
/// One Cockpit pane as it was when AgentHub last saved its layout, in pane order. On start each entry gets a pane back:
/// reattached to its hosted session when that host still runs, else the launcher on the same folder and CLI with the
/// session offered for resume.
/// </summary>
public sealed class PaneLayoutEntry
{
    /// <summary>Hosted session id while a session was running; null for an idle pane.</summary>
    public string? SessionId { get; set; }
    public string? Folder { get; set; }
    /// <summary>The CLI's plain command (never a resume command), as in the launcher's function types.</summary>
    public string? Command { get; set; }
    public string? ShellName { get; set; }
    public bool IsAgent { get; set; }
    public string? RepoName { get; set; }
    /// <summary>Transcript of the pane's last Claude Code / Codex session, for the resume offer.</summary>
    public string? TranscriptPath { get; set; }
}
