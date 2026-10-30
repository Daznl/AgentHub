using AgentHub.Services.Context;

namespace AgentHub.Services.History;

/// <summary>Continues an earlier Claude Code or Codex session with the CLI's own resume command.</summary>
public static class SessionResume
{
    /// <summary>
    /// The id the CLI resumes by. Claude Code names transcripts <c>&lt;session id&gt;.jsonl</c>; Codex names rollouts
    /// <c>rollout-&lt;timestamp&gt;-&lt;session id&gt;.jsonl</c>, the id being the trailing 36-character UUID.
    /// </summary>
    public static string SessionId(SessionSummary session)
    {
        var name = Path.GetFileNameWithoutExtension(session.FilePath);
        return session.Agent == ContextSource.Codex && name.Length >= 36 ? name[^36..] : name;
    }

    /// <summary>The configured agent command plus the resume argument: <c>--resume &lt;id&gt;</c> or Codex's <c>resume &lt;id&gt;</c>.</summary>
    public static string BuildCommand(string agentCommand, SessionSummary session) =>
        session.Agent == ContextSource.Codex
            ? $"{agentCommand} resume {SessionId(session)}"
            : $"{agentCommand} --resume {SessionId(session)}";

    /// <summary>
    /// A session listed from AgentHub's archive may no longer exist where the CLI looks for it. Copies it back (with
    /// Claude's companion <c>&lt;session id&gt;\</c> folder of sub-agent transcripts and tool output) so resume finds it.
    /// Never overwrites a live file. Returns false when the copy failed.
    /// </summary>
    public static bool RestoreFromArchive(SessionSummary session, string? archiveRoot,
        string? claudeProjectsDir = null, string? codexSessionsDir = null)
    {
        if (archiveRoot is null) return true;
        var (archiveDir, liveDir) = session.Agent == ContextSource.Codex
            ? (HistoryArchiver.CodexArchiveDir(archiveRoot), codexSessionsDir ?? SessionContextReader.CodexSessionsDir)
            : (HistoryArchiver.ClaudeArchiveDir(archiveRoot), claudeProjectsDir ?? SessionContextReader.ClaudeProjectsDir);

        var relative = Path.GetRelativePath(archiveDir, session.FilePath);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative)) return true; // already live

        try
        {
            var target = Path.Combine(liveDir, relative);
            if (!File.Exists(target))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(session.FilePath, target);
            }

            var companion = Path.Combine(Path.GetDirectoryName(session.FilePath)!, Path.GetFileNameWithoutExtension(session.FilePath));
            if (Directory.Exists(companion))
            {
                var liveCompanion = Path.Combine(Path.GetDirectoryName(target)!, Path.GetFileName(companion));
                foreach (var file in Directory.EnumerateFiles(companion, "*", SearchOption.AllDirectories))
                {
                    var dest = Path.Combine(liveCompanion, Path.GetRelativePath(companion, file));
                    if (File.Exists(dest)) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    File.Copy(file, dest);
                }
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
