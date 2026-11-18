using System.Text.Json;
using System.Text.Json.Serialization;
using AgentHub.Hosting;
using AgentHub.Services.Context;
using AgentHub.Services.Timeline;

namespace AgentHub.Services.World;

/// <summary>
/// What the 3D world (the Godot project in <c>world/</c>) draws: every project with its git tree and files, the coding
/// sessions running now, and the orchestrators over them. AgentHub's <see cref="WorldService"/> writes it as
/// <c>world.json</c> when the structure changes; what sessions do moment to moment goes to <see cref="EventsFile"/>
/// (<see cref="WorldEvent"/>, one JSON object per line), which Godot tails. AgentHub is the only reader of git, GitHub
/// and transcripts; Godot only draws. Godot refuses a newer <see cref="Schema"/> rather than drawing it wrongly.
/// </summary>
public sealed record WorldSnapshot(
    int Schema,
    DateTimeOffset GeneratedAt,
    string EventsFile,
    IReadOnlyList<WorldProject> Projects,
    IReadOnlyList<WorldLiveSession> Live,
    IReadOnlyList<WorldOrchestrator> Orchestrators)
{
    public const int CurrentSchema = 2;
}

/// <summary>
/// One repository (or plain folder). Rows are the Timeline's rows (one per session or run of outside commits, oldest
/// first), so height in the world follows the 2D tree's order instead of clock time, which has days-long gaps.
/// <see cref="Folders"/> are the main folder and every worktree; a path in any of them is this project's.
/// </summary>
public sealed record WorldProject(
    string Id, string Name, string Root, string Trunk, bool IsGit, IReadOnlyList<string> Folders,
    IReadOnlyList<WorldRow> Rows,
    IReadOnlyList<WorldBranch> Branches,
    IReadOnlyList<WorldCommit> Commits,
    IReadOnlyList<WorldSession> Sessions,
    IReadOnlyList<WorldPullRequest> PullRequests,
    IReadOnlyList<WorldWorktree> Worktrees,
    IReadOnlyList<WorldFileEntry> Files,
    bool FilesTruncated);

/// <summary>One Timeline row: its index is the height in the world.</summary>
public sealed record WorldRow(int Index, string Kind, DateTimeOffset Time, string? Branch);

/// <summary>
/// A branch lane. <see cref="Column"/> and <see cref="Colour"/> are the 2D tree's, so a branch is recognisably the same
/// in both views. <see cref="StartRow"/>/<see cref="EndRow"/> bound the lane; <see cref="Parent"/> is the lane it forked from.
/// </summary>
public sealed record WorldBranch(string Key, string Name, bool IsTrunk, bool IsImported, bool IsMerged, bool IsSquash, bool IsLocalOnly,
    string? Parent, int Column, int Colour, int StartRow, int EndRow, string? MergeHash, int CommitCount);

public sealed record WorldCommit(string Hash, string Short, IReadOnlyList<string> Parents, DateTimeOffset Time, string Author, string Subject,
    string? Branch, int Row, string? Session);

/// <summary>A past or present coding session from its transcript. <see cref="Id"/> is the transcript path, which is stable.</summary>
public sealed record WorldSession(string Id, string Agent, string? Title, DateTimeOffset StartedAt, DateTimeOffset EndedAt, bool IsLive,
    string? Folder, string? Branch, int Row, IReadOnlyList<string> Commits);

public sealed record WorldPullRequest(int Number, string Title, string Head, string Base, string State, bool IsDraft, DateTimeOffset? MergedAt,
    string? MergeCommit, string? Author);

/// <summary>A git worktree: a folder holding a checkout of a branch.</summary>
public sealed record WorldWorktree(string Path, string? Branch, string? Head, bool IsMain);

/// <summary>A tracked file, its path relative to the project root with forward slashes, and its size in bytes.</summary>
public sealed record WorldFileEntry(string Path, long Size);

/// <summary>
/// A coding session running now (its transcript was written to lately): one crab in the world. <see cref="Id"/> is the
/// transcript path, as in <see cref="WorldSession"/> and <see cref="WorldEvent.Session"/>. <see cref="Pane"/> names the
/// Cockpit pane it runs in, when AgentHub hosts it.
/// </summary>
public sealed record WorldLiveSession(string Id, string Agent, string? Title, string Project, string Folder, string? Branch,
    DateTimeOffset LastActivity, string? Pane);

/// <summary>A pane orchestrator over a live session (<see cref="Worker"/> is that session's id), with the step its run is on.</summary>
public sealed record WorldOrchestrator(string Id, string Title, string? Worker, string State, string? Step);

/// <summary>
/// Something a session or orchestrator just did, as a line of the events file. <see cref="Kind"/>: read, edit, write,
/// search, bash, build, test, commit, push, merge, pr, branch, snowflake, azure, web, agent, tool, prompt, reply,
/// orchestrate. <see cref="File"/> is relative to <see cref="Project"/>'s root when the target is one of its files.
/// </summary>
public sealed record WorldEvent(long Seq, DateTimeOffset Time, string Session, string Kind, string? Project, string? File, string? Text,
    string? Orchestrator = null);

public static class WorldSnapshotBuilder
{
    public static WorldProject BuildProject(TimelineScope scope, TimelineModel model, IReadOnlyList<PullRequestFact>? pullRequests,
        IReadOnlyList<WorldWorktree> worktrees, IReadOnlyList<WorldFileEntry> files, bool filesTruncated)
    {
        var rows = new List<WorldRow>();
        var commits = new List<WorldCommit>();
        var sessions = new List<WorldSession>();
        for (var i = 0; i < model.Rows.Count; i++)
        {
            var row = model.Rows[i];
            var lane = row.Branch?.Key ?? (row.IsHollow ? null : TimelineBranch.TrunkKey);
            rows.Add(new WorldRow(i, row.Kind.ToString().ToLowerInvariant(), row.Time, lane));
            var sessionId = row.Session?.Summary.FilePath;
            foreach (var c in row.Commits)
                commits.Add(new WorldCommit(c.Hash, c.Short, c.Parents, c.Time, c.Author, c.Subject, c.Branch, i, sessionId));
            if (row.Session is { } s)
                sessions.Add(new WorldSession(s.Summary.FilePath, AgentName(s.Summary.Agent), s.Summary.Title ?? Shorten(s.Summary.FirstPrompt),
                    s.Summary.StartedAt, s.EndedAt, row.IsLive, s.Folder ?? s.Summary.Folder, lane, i, row.Commits.Select(c => c.Hash).ToList()));
        }

        var branches = model.Branches.Select(b => new WorldBranch(b.Key, b.Name, b.IsTrunk, b.IsImported, b.MergeHash is not null, b.IsSquash,
            b.IsLocalOnly, b.ParentKey, b.Column, b.ColourIndex, b.StartRow, b.EndRow, b.MergeHash, b.Commits.Count)).ToList();

        var prs = (pullRequests ?? []).Select(p => new WorldPullRequest(p.Number, p.Title, p.Head, p.Base, p.State, p.IsDraft, p.MergedAt,
            p.MergeCommit, p.Author)).ToList();

        return new WorldProject(ProjectId(scope.Root), scope.Name, scope.Root, model.TrunkName, scope.IsGit, scope.Folders,
            rows, branches, commits, sessions, prs, worktrees, files, filesTruncated);
    }

    /// <summary>A short id that stays the same for a root folder, whatever its letter case.</summary>
    public static string ProjectId(string root) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(TimelineSource.NormalisePath(root).ToLowerInvariant())))[..10].ToLowerInvariant();

    public static string AgentName(ContextSource source) => source switch
    {
        ContextSource.ClaudeCode => "claude",
        ContextSource.Codex => "codex",
        _ => "other"
    };

    public static string? Shorten(string? text, int max = 80)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var line = text.Trim().Split('\n')[0].Trim().Replace("\r", "");
        return line.Length > max ? line[..(max - 1)] + "…" : line;
    }

    /// <summary>
    /// Parses <c>git worktree list --porcelain</c>: blocks of <c>worktree</c>, <c>HEAD</c>, <c>branch refs/heads/x</c>
    /// (or <c>detached</c>), separated by blank lines. The first block is the main working tree.
    /// </summary>
    public static List<WorldWorktree> ParseWorktrees(string porcelain)
    {
        var list = new List<WorldWorktree>();
        string? path = null, head = null, branch = null;
        void Flush()
        {
            if (path is not null) list.Add(new WorldWorktree(path, branch, head, list.Count == 0));
            path = head = branch = null;
        }
        foreach (var raw in porcelain.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) { Flush(); continue; }
            if (line.StartsWith("worktree ", StringComparison.Ordinal)) { Flush(); path = TimelineSource.NormalisePath(line["worktree ".Length..].Trim()); }
            else if (line.StartsWith("HEAD ", StringComparison.Ordinal)) head = line["HEAD ".Length..].Trim();
            else if (line.StartsWith("branch ", StringComparison.Ordinal))
            {
                var name = line["branch ".Length..].Trim();
                branch = name.StartsWith("refs/heads/", StringComparison.Ordinal) ? name["refs/heads/".Length..] : name;
            }
        }
        Flush();
        return list;
    }

    /// <summary>
    /// <c>git ls-files -z</c> output with each file's size, largest kept first when there are more than <paramref name="max"/>
    /// (a city of every file in a huge repository would be unreadable and slow); the rest in path order.
    /// </summary>
    public static (List<WorldFileEntry> Files, bool Truncated) ParseFiles(string lsFilesZ, string root, int max)
    {
        var files = new List<WorldFileEntry>();
        foreach (var rel in lsFilesZ.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            long size = 0;
            try
            {
                var info = new FileInfo(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));
                if (!info.Exists) continue; // deleted in the working tree
                size = info.Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
            files.Add(new WorldFileEntry(rel.Replace('\\', '/'), size));
        }
        var truncated = files.Count > max;
        if (truncated) files = files.OrderByDescending(f => f.Size).Take(max).ToList();
        files.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return (files, truncated);
    }

    /// <summary>The project a path belongs to (inside its root or one of its worktrees) and the path relative to that folder.</summary>
    public static (WorldProject Project, string Relative)? Locate(IEnumerable<WorldProject> projects, string path)
    {
        string full;
        try { full = TimelineSource.NormalisePath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
        (WorldProject, string)? best = null;
        var bestLength = -1;
        foreach (var project in projects)
            foreach (var folder in project.Folders)
            {
                var f = folder.TrimEnd('\\', '/');
                if (f.Length <= bestLength) continue;
                if (full.Equals(f, StringComparison.OrdinalIgnoreCase)) { best = (project, ""); bestLength = f.Length; }
                else if (full.StartsWith(f + "\\", StringComparison.OrdinalIgnoreCase)) { best = (project, full[(f.Length + 1)..].Replace('\\', '/')); bestLength = f.Length; }
            }
        return best;
    }
}

/// <summary>Where the world's files live, and writing them so Godot never reads half a file.</summary>
public static class WorldStore
{
    public const string DefaultRoot = @"C:\data\agenthub\world";

    public static string Root => Environment.GetEnvironmentVariable("AGENTHUB_WORLD_DIR") is { Length: > 0 } custom ? custom : DefaultRoot;

    public static string WorldPath(string? root = null) => Path.Combine(root ?? Root, "world.json");

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);

    /// <summary>Writes through a temporary file and a replace, so the reader sees the old file or the new one, never a mix.</summary>
    public static void Write(string path, WorldSnapshot snapshot)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, Serialize(snapshot));
        SharedFile.Replace(temp, path);
    }
}
