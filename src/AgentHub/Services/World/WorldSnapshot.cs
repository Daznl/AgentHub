using System.Text.Json;
using System.Text.Json.Serialization;
using AgentHub.Hosting;
using AgentHub.Services.Context;
using AgentHub.Services.Timeline;

namespace AgentHub.Services.World;

/// <summary>
/// What the 3D world (the Godot project in <c>world/</c>) draws, written by AgentHub as <c>world.json</c> and read by
/// Godot whenever the file changes. AgentHub is the only reader of git, GitHub and transcripts; Godot only draws this.
/// The format is versioned by <see cref="Schema"/>; Godot refuses a newer one rather than drawing it wrongly.
/// Rows are the Timeline's rows (one per session or run of outside commits, oldest first), so the world's height
/// matches the 2D tree's order instead of clock time, which has days-long gaps.
/// </summary>
public sealed record WorldSnapshot(
    int Schema,
    DateTimeOffset GeneratedAt,
    WorldRepo Repo,
    IReadOnlyList<WorldRow> Rows,
    IReadOnlyList<WorldBranch> Branches,
    IReadOnlyList<WorldCommit> Commits,
    IReadOnlyList<WorldSession> Sessions,
    IReadOnlyList<WorldPullRequest> PullRequests,
    IReadOnlyList<WorldWorktree> Worktrees,
    IReadOnlyList<WorldPane> Panes)
{
    public const int CurrentSchema = 1;
}

public sealed record WorldRepo(string Name, string Root, string Trunk, bool IsGit);

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

/// <summary>A coding session from its transcript. <see cref="Id"/> is the transcript path, which is stable.</summary>
public sealed record WorldSession(string Id, string Agent, string? Title, DateTimeOffset StartedAt, DateTimeOffset EndedAt, bool IsLive,
    string? Folder, string? Branch, int Row, IReadOnlyList<string> Commits);

public sealed record WorldPullRequest(int Number, string Title, string Head, string Base, string State, bool IsDraft, DateTimeOffset? MergedAt,
    string? MergeCommit, string? Author);

/// <summary>A git worktree: a folder holding a checkout of a branch.</summary>
public sealed record WorldWorktree(string Path, string? Branch, string? Head, bool IsMain);

/// <summary>A terminal AgentHub hosts (a Cockpit pane's session) that runs in one of the repository's folders right now.</summary>
public sealed record WorldPane(string Id, string Title, string Folder, bool IsAgent, string Command, DateTimeOffset StartedAt);

public static class WorldSnapshotBuilder
{
    public static WorldSnapshot Build(TimelineScope scope, TimelineModel model, IReadOnlyList<PullRequestFact>? pullRequests,
        IReadOnlyList<WorldWorktree> worktrees, IReadOnlyList<WorldPane> panes, DateTimeOffset now)
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

        return new WorldSnapshot(WorldSnapshot.CurrentSchema, now, new WorldRepo(scope.Name, scope.Root, model.TrunkName, scope.IsGit),
            rows, branches, commits, sessions, prs, worktrees, panes);
    }

    public static string AgentName(ContextSource source) => source switch
    {
        ContextSource.ClaudeCode => "claude",
        ContextSource.Codex => "codex",
        _ => "other"
    };

    private static string? Shorten(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var line = text.Trim().Split('\n')[0].Trim();
        return line.Length > 80 ? line[..79] + "…" : line;
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

    /// <summary>Live hosted sessions whose folder is one of the scope's folders or inside one.</summary>
    public static List<WorldPane> PanesIn(TimelineScope scope, IEnumerable<SessionRecord> live) =>
        live.Where(r => !string.IsNullOrWhiteSpace(r.WorkingDirectory) && scope.Folders.Any(f => IsInside(r.WorkingDirectory, f)))
            .Select(r => new WorldPane(r.Id, r.Title, TimelineSource.NormalisePath(r.WorkingDirectory), r.IsAgent, r.CommandLine, r.StartedAt))
            .ToList();

    private static bool IsInside(string folder, string root)
    {
        var a = TimelineSource.NormalisePath(folder).TrimEnd('\\', '/');
        var b = TimelineSource.NormalisePath(root).TrimEnd('\\', '/');
        return a.Equals(b, StringComparison.OrdinalIgnoreCase) || a.StartsWith(b + "\\", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Where a repository's world file lives, and writing it so Godot never reads half a file.</summary>
public static class WorldFile
{
    public const string DefaultRoot = @"C:\data\agenthub\world";

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    /// <summary><c>C:\data\agenthub\world\&lt;repo&gt;-&lt;hash of root&gt;\world.json</c>: one file per repository, names never collide.</summary>
    public static string PathFor(TimelineScope scope, string? root = null)
    {
        var baseDir = string.IsNullOrWhiteSpace(root) ? Environment.GetEnvironmentVariable("AGENTHUB_WORLD_DIR") ?? DefaultRoot : root;
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(scope.Root.ToLowerInvariant())))[..8].ToLowerInvariant();
        var safe = string.Concat(scope.Name.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) || ch == ' ' ? '-' : ch));
        return Path.Combine(baseDir, $"{safe}-{hash}", "world.json");
    }

    public static string Serialize(WorldSnapshot snapshot) => JsonSerializer.Serialize(snapshot, JsonOptions);

    /// <summary>Writes through a temporary file and a replace, so the reader sees the old file or the new one, never a mix.</summary>
    public static void Write(string path, WorldSnapshot snapshot)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, Serialize(snapshot));
        SharedFile.Replace(temp, path);
    }
}
