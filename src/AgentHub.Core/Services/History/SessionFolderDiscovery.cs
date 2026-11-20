using AgentHub.Services.Context;

namespace AgentHub.Services.History;

/// <summary>A folder that Claude Code or Codex sessions ran in, found from the transcripts on this computer.</summary>
/// <param name="Exists">Whether the folder is still on disk. A deleted folder's sessions can still be read, not continued.</param>
public sealed record SessionFolder(string Path, int ClaudeSessions, int CodexSessions, DateTimeOffset LastActivity, bool Exists)
{
    public int Sessions => ClaudeSessions + CodexSessions;
}

/// <summary>
/// Every folder with a recorded session, so a new AgentHub install shows the sessions its user already has without
/// registering each folder first. Claude Code and Codex keep their transcripts on this computer (there is no account-wide
/// copy to fetch), so this reads the same files as <see cref="SessionHistoryReader"/>, plus AgentHub's archive, and
/// takes each session's folder from what the CLI recorded, never from Claude Code's lossy folder-name encoding.
/// A folder counts the same sessions <see cref="SessionHistoryReader.ListSessions"/> lists for it.
/// </summary>
public static class SessionFolderDiscovery
{
    /// <summary>Folders with sessions, newest activity first.</summary>
    public static IReadOnlyList<SessionFolder> Discover(string? claudeProjectsDir = null, string? codexSessionsDir = null,
        string? archiveRoot = null)
    {
        var claude = Transcripts(ClaudeFiles(claudeProjectsDir ?? SessionContextReader.ClaudeProjectsDir),
            archiveRoot is null ? [] : ClaudeFiles(HistoryArchiver.ClaudeArchiveDir(archiveRoot)));
        var codex = Transcripts(CodexFiles(codexSessionsDir ?? SessionContextReader.CodexSessionsDir),
            archiveRoot is null ? [] : CodexFiles(HistoryArchiver.CodexArchiveDir(archiveRoot)));

        var sessions = claude.Select(t => (t.Path, t.ProjectDir, Agent: ContextSource.ClaudeCode))
            .Concat(codex.Select(t => (t.Path, t.ProjectDir, Agent: ContextSource.Codex)))
            .AsParallel().WithDegreeOfParallelism(4)
            .Select(t => (t.ProjectDir, Summary: SessionHistoryReader.CachedSummary(t.Path, t.Agent)))
            .Where(t => t.Summary?.Folder is { } folder && (t.ProjectDir is null || IsClaudeProjectOf(t.ProjectDir, folder)))
            .Select(t => t.Summary!)
            .ToList();

        return sessions
            .GroupBy(s => s.Folder!, StringComparer.OrdinalIgnoreCase)
            .Select(g => new SessionFolder(g.Key,
                g.Count(s => s.Agent == ContextSource.ClaudeCode),
                g.Count(s => s.Agent == ContextSource.Codex),
                g.Max(s => s.LastActivity),
                FolderExists(g.Key)))
            .OrderByDescending(f => f.LastActivity)
            .ToList();
    }

    /// <summary>
    /// Folders that only ever hold throwaway work: anything under the user's AppData (desktop apps' scratch workspaces)
    /// or the temp folder. Their sessions stay readable in History by browsing; they are not offered as places to work.
    /// </summary>
    public static bool IsTemporary(string folder)
    {
        foreach (var root in TemporaryRoots)
            if (IsUnder(folder, root)) return true;
        return false;
    }

    /// <summary>AgentHub's data folder: orchestrator sessions, the Template helper, reviews, test runs and scratch.</summary>
    public static string AgentHubDataRoot => AgentHub.Hosting.AgentHubData.Root;

    /// <summary>Git worktrees made for tasks inside <see cref="AgentHubDataRoot"/>: real project work, so they are offered.</summary>
    public static string AgentHubWorktreesRoot { get; } = AgentHub.Hosting.AgentHubData.Folder("worktrees");

    /// <summary>
    /// Folders AgentHub runs its own Claude sessions in (orchestrators, the Template helper, reviews, tests): AgentHub's
    /// working space, not the user's projects. Worktrees under the data folder are project work and are not counted.
    /// </summary>
    public static bool IsAgentHubOwn(string folder) =>
        (IsUnder(folder, AgentHubDataRoot) && !IsUnder(folder, AgentHubWorktreesRoot)) ||
        IsUnder(folder, Orchestrator.PaneOrchestrator.DefaultSessionsRoot.TrimEnd('\\')) ||
        IsUnder(folder, Orchestrator.TemplateHelper.WorkingDirectory) ||
        IsUnder(folder, Instructions.ClaudePrint.WorkingDirectory);

    private static readonly string[] TemporaryRoots = BuildTemporaryRoots();

    private static string[] BuildTemporaryRoots()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new[]
            {
                string.IsNullOrEmpty(profile) ? "" : Path.Combine(profile, "AppData"),
                string.IsNullOrEmpty(profile) || OperatingSystem.IsWindows() ? "" : Path.Combine(profile, ".cache"),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                Path.GetTempPath()
            }
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => r.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsUnder(string folder, string root) =>
        folder.Equals(root, StringComparison.OrdinalIgnoreCase) ||
        folder.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    // Claude Code files a session under its folder's encoded name, shortened with a hash past 200 characters. A session
    // whose recorded folder encodes to another name would not be found by ListSessions, so it is not credited here.
    private static bool IsClaudeProjectOf(string projectDir, string folder)
    {
        var encoded = SessionContextReader.EncodeClaudeProjectName(folder);
        return encoded.Length > 200
            ? projectDir.StartsWith(encoded[..200], StringComparison.OrdinalIgnoreCase)
            : projectDir.Equals(encoded, StringComparison.OrdinalIgnoreCase);
    }

    private static bool FolderExists(string folder)
    {
        try { return Directory.Exists(folder); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    // One copy per transcript: the live one, or the archived one when it is longer (as ListSessions picks). Keyed by
    // project folder and file name for Claude Code, by file name for Codex (its names hold a timestamp and id).
    private static IEnumerable<(string Path, string? ProjectDir)> Transcripts(
        IEnumerable<(string Path, string? ProjectDir)> live, IEnumerable<(string Path, string? ProjectDir)> archived)
    {
        var chosen = new Dictionary<string, (string Path, string? ProjectDir)>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in live) chosen[Key(t)] = t;
        foreach (var t in archived)
        {
            if (Path.GetFileName(t.Path).Contains(".superseded-", StringComparison.OrdinalIgnoreCase)) continue;
            var key = Key(t);
            if (!chosen.TryGetValue(key, out var current) || FileLength(t.Path) > FileLength(current.Path)) chosen[key] = t;
        }
        return chosen.Values;

        static string Key((string Path, string? ProjectDir) t) => (t.ProjectDir ?? "") + "/" + Path.GetFileName(t.Path);
    }

    // Top level of each project folder only: sub-agent transcripts live in <session>/subagents/.
    private static IEnumerable<(string Path, string? ProjectDir)> ClaudeFiles(string projectsDir)
    {
        try
        {
            if (!Directory.Exists(projectsDir)) return [];
            return Directory.EnumerateDirectories(projectsDir)
                .SelectMany(dir => Directory.EnumerateFiles(dir, "*.jsonl", SearchOption.TopDirectoryOnly)
                    .Select(file => (file, (string?)Path.GetFileName(dir))))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static IEnumerable<(string Path, string? ProjectDir)> CodexFiles(string sessionsDir)
    {
        try
        {
            if (!Directory.Exists(sessionsDir)) return [];
            return Directory.EnumerateFiles(sessionsDir, "rollout-*.jsonl", SearchOption.AllDirectories)
                .Select(file => (file, (string?)null))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static long FileLength(string path)
    {
        try { return new FileInfo(path).Length; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }
}
