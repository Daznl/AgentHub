using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using AgentHub.Services.History;

namespace AgentHub.Services.Timeline;

/// <summary>
/// What one timeline covers: a Git repository with every worktree (sessions often run in a worktree beside the main
/// folder), or, outside Git, the one folder.
/// </summary>
public sealed record TimelineScope(string Root, string Name, IReadOnlyList<string> Folders, bool IsGit);

/// <summary>Reads what the timeline is built from: the repository's worktrees and commits (via git) and its sessions' transcripts.</summary>
public static partial class TimelineSource
{
    private const char Sep = '\u001f';

    public static async Task<TimelineScope> ResolveAsync(ProcessRunner runner, string folder, CancellationToken cancellationToken = default)
    {
        var top = await RunGitAsync(runner, folder, ["rev-parse", "--show-toplevel"], cancellationToken);
        if (top is null || string.IsNullOrWhiteSpace(top)) return new TimelineScope(folder, FolderName(folder), [folder], IsGit: false);

        var folders = new List<string>();
        var list = await RunGitAsync(runner, top.Trim(), ["worktree", "list", "--porcelain"], cancellationToken) ?? "";
        foreach (var line in list.Split('\n'))
            if (line.StartsWith("worktree ", StringComparison.Ordinal)) folders.Add(NormalisePath(line["worktree ".Length..].Trim()));
        if (folders.Count == 0) folders.Add(NormalisePath(top.Trim()));

        // The first worktree listed is the main one; it names the timeline.
        return new TimelineScope(folders[0], FolderName(folders[0]), folders.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), IsGit: true);
    }

    /// <summary>Every commit reachable from a local or remote branch, newest first as git gives them (stashes and tags left out).</summary>
    public static async Task<List<TimelineCommit>> ReadCommitsAsync(ProcessRunner runner, TimelineScope scope, CancellationToken cancellationToken = default)
    {
        if (!scope.IsGit) return [];
        var output = await RunGitAsync(runner, scope.Root,
            ["log", "--branches", "--remotes", $"--format=%H{Sep}%P{Sep}%cI{Sep}%an{Sep}%s"], cancellationToken) ?? "";
        return ParseLog(output);
    }

    public static List<TimelineCommit> ParseLog(string output)
    {
        var commits = new List<TimelineCommit>();
        foreach (var line in output.Split('\n'))
        {
            var f = line.TrimEnd('\r').Split(Sep);
            if (f.Length < 5 || f[0].Length < 7) continue;
            if (!DateTimeOffset.TryParse(f[2], CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)) continue;
            var parents = f[1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            commits.Add(new TimelineCommit(f[0], parents, time, f[3], f[4]));
        }
        return commits;
    }

    /// <summary>Every local and remote branch head. Remote names lose their remote prefix; the symbolic <c>origin/HEAD</c> is left out.</summary>
    public static async Task<List<TimelineBranchTip>> ReadBranchTipsAsync(ProcessRunner runner, TimelineScope scope, CancellationToken cancellationToken = default)
    {
        if (!scope.IsGit) return [];
        var output = await RunGitAsync(runner, scope.Root,
            ["for-each-ref", $"--format=%(objectname){Sep}%(refname){Sep}%(symref)", "refs/heads", "refs/remotes"], cancellationToken) ?? "";
        return ParseTips(output);
    }

    public static List<TimelineBranchTip> ParseTips(string output)
    {
        var tips = new List<TimelineBranchTip>();
        foreach (var line in output.Split('\n'))
        {
            var f = line.TrimEnd('\r').Split(Sep);
            if (f.Length < 2 || f[0].Length < 7) continue;
            if (f.Length > 2 && f[2].Length > 0) continue; // a symbolic ref such as refs/remotes/origin/HEAD
            var name = f[1];
            if (name.StartsWith("refs/heads/", StringComparison.Ordinal)) tips.Add(new TimelineBranchTip(f[0], name["refs/heads/".Length..], IsRemote: false));
            else if (name.StartsWith("refs/remotes/", StringComparison.Ordinal))
            {
                var rest = name["refs/remotes/".Length..];
                var slash = rest.IndexOf('/');
                if (slash <= 0 || slash == rest.Length - 1) continue;
                var branch = rest[(slash + 1)..];
                if (branch == "HEAD") continue;
                tips.Add(new TimelineBranchTip(f[0], branch, IsRemote: true));
            }
        }
        return tips;
    }

    /// <summary>
    /// The hashes of every commit (on any branch) that touched one of <paramref name="paths"/>, relative to the root. Used to
    /// tell which sessions kept the handoff and docs up to date.
    /// </summary>
    public static async Task<HashSet<string>> ReadTouchingAsync(ProcessRunner runner, TimelineScope scope, IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
    {
        if (!scope.IsGit || paths.Count == 0) return [];
        var output = await RunGitAsync(runner, scope.Root, ["log", "--branches", "--remotes", "--format=%H", "--name-only", "--", .. paths], cancellationToken) ?? "";
        var hashes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in output.Split('\n'))
        {
            var t = line.Trim();
            if (t.Length == 40 && t.All(Uri.IsHexDigit)) hashes.Add(t);
        }
        return hashes;
    }

    /// <summary>
    /// The repository's pull requests from GitHub through <c>gh</c>, newest first; null when gh is missing, not signed in,
    /// the repository has no GitHub remote, or the call fails for any other reason (the review then says GitHub facts are unavailable).
    /// </summary>
    public static async Task<List<PullRequestFact>?> ReadPullRequestsAsync(ProcessRunner runner, TimelineScope scope, CancellationToken cancellationToken = default)
    {
        if (!scope.IsGit || !Directory.Exists(scope.Root)) return null;
        try
        {
            var (exit, stdout, _) = await runner.RunAsync("gh",
                ["pr", "list", "--state", "all", "--limit", "100", "--json", "number,title,headRefName,baseRefName,author,mergedBy,mergedAt,mergeCommit,reviews,isDraft,state"],
                scope.Root, cancellationToken, TimeSpan.FromSeconds(30));
            return exit == 0 ? ParsePullRequests(stdout) : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null; // gh not installed
        }
    }

    /// <summary>Parses <c>gh pr list --json …</c> output; null when it is not a JSON array.</summary>
    public static List<PullRequestFact>? ParsePullRequests(string json)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array) return null;
            var list = new List<PullRequestFact>();
            foreach (var pr in doc.RootElement.EnumerateArray())
            {
                static string Str(System.Text.Json.JsonElement e, string name) =>
                    e.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() ?? "" : "";
                static string? Login(System.Text.Json.JsonElement e, string name) =>
                    e.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.Object ? Str(v, "login") : null;
                var number = pr.TryGetProperty("number", out var n) && n.ValueKind == System.Text.Json.JsonValueKind.Number ? n.GetInt32() : 0;
                if (number == 0) continue;
                var mergedAt = DateTimeOffset.TryParse(Str(pr, "mergedAt"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var m) ? m : (DateTimeOffset?)null;
                var mergeCommit = pr.TryGetProperty("mergeCommit", out var mc) && mc.ValueKind == System.Text.Json.JsonValueKind.Object ? Str(mc, "oid") : null;
                var reviews = pr.TryGetProperty("reviews", out var r) && r.ValueKind == System.Text.Json.JsonValueKind.Array ? r.GetArrayLength() : 0;
                var draft = pr.TryGetProperty("isDraft", out var d) && d.ValueKind == System.Text.Json.JsonValueKind.True;
                list.Add(new PullRequestFact(number, Str(pr, "title"), Str(pr, "headRefName"), Str(pr, "baseRefName"), Login(pr, "author"), Login(pr, "mergedBy"),
                    mergedAt, string.IsNullOrEmpty(mergeCommit) ? null : mergeCommit, reviews, draft, Str(pr, "state")));
            }
            return list;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>Claude Code and Codex sessions recorded in any of the scope's folders, live or archived, each once.</summary>
    public static List<TimelineSessionInput> ReadSessions(TimelineScope scope, string? archiveRoot)
    {
        return scope.Folders
            .SelectMany(folder => SessionHistoryReader.ListSessions(folder, archiveRoot: archiveRoot).Select(s => (Folder: folder, Summary: s)))
            .GroupBy(s => Path.GetFileName(s.Summary.FilePath), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.MaxBy(s => s.Summary.SizeBytes))
            .Select(s =>
            {
                var facts = TranscriptScan.Read(s.Summary.FilePath);
                var path = s.Summary.FilePath;
                return new TimelineSessionInput(s.Summary, LastEntryTime(path) ?? s.Summary.LastActivity, facts.Hashes, s.Folder, facts.ActiveMinutes,
                    (text, from, to) => TranscriptScan.MentionsBetween(path, text, from, to));
            })
            .ToList();
    }

    [GeneratedRegex("\"timestamp\"\\s*:\\s*\"([^\"]+)\"")]
    private static partial Regex TimestampRegex();

    /// <summary>
    /// Time of the transcript's last entry. Needed because an archived copy's file time is when it was copied, not when
    /// the session last did something.
    /// </summary>
    public static DateTimeOffset? LastEntryTime(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var length = (int)Math.Min(stream.Length, 128 * 1024);
            stream.Seek(-length, SeekOrigin.End);
            var buffer = new byte[length];
            stream.ReadExactly(buffer);
            var text = Encoding.UTF8.GetString(buffer);
            DateTimeOffset? latest = null;
            foreach (Match m in TimestampRegex().Matches(text))
                if (DateTimeOffset.TryParse(m.Groups[1].Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) && (latest is null || t > latest))
                    latest = t;
            return latest;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static async Task<string?> RunGitAsync(ProcessRunner runner, string folder, string[] args, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(folder)) return null;
        try
        {
            var (exit, stdout, _) = await runner.RunAsync("git", ["-C", folder, .. args], folder, cancellationToken, TimeSpan.FromSeconds(60));
            return exit == 0 ? stdout : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null; // git not installed
        }
    }

    private static string NormalisePath(string path) =>
        Path.GetFullPath(path.Replace('/', Path.DirectorySeparatorChar)).TrimEnd(Path.DirectorySeparatorChar);

    private static string FolderName(string folder) =>
        Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) is { Length: > 0 } n ? n : folder;
}

/// <summary>One pull request as GitHub reports it through <c>gh pr list</c>. <see cref="State"/> is OPEN, MERGED or CLOSED.</summary>
public sealed record PullRequestFact(int Number, string Title, string Head, string Base, string? Author, string? MergedBy, DateTimeOffset? MergedAt,
    string? MergeCommit, int Reviews, bool IsDraft, string State)
{
    public bool IsMerged => MergedAt is not null || string.Equals(State, "MERGED", StringComparison.OrdinalIgnoreCase);
}

/// <summary>What one transcript scan found: hash prefixes mentioned, and the minutes (UTC, since the epoch) in which the session wrote an entry.</summary>
public sealed record TranscriptFacts(IReadOnlySet<string> Hashes, IReadOnlySet<long> ActiveMinutes)
{
    public static readonly TranscriptFacts Empty = new(new HashSet<string>(), new HashSet<long>());
}

/// <summary>Kept for callers that only need the hashes; see <see cref="TranscriptScan"/>.</summary>
public static class TranscriptHashes
{
    public static IReadOnlySet<string> Read(string path) => TranscriptScan.Read(path).Hashes;
}

/// <summary>
/// One pass over a transcript for two things the timeline needs: the commit hash prefixes it mentions (first 7 hex
/// characters of every 7-to-40 character hex word) and the minutes in which it wrote an entry (every
/// <c>"timestamp":"…"</c>), so a session open for days only claims commits made while it was actually doing something.
/// Scans are incremental: a growing live transcript is only read from where the last scan stopped.
/// </summary>
public static class TranscriptScan
{
    private sealed record Scanned(long Length, HashSet<string> Hashes, HashSet<long> Minutes, SortedList<long, long> MinuteOffsets);

    private static readonly ConcurrentDictionary<string, Scanned> Cache = new(StringComparer.OrdinalIgnoreCase);
    private const int ChunkBytes = 1024 * 1024;
    private static readonly byte[] TimestampKey = "\"timestamp\":\""u8.ToArray();

    public static TranscriptFacts Read(string path) => Scan(path) is { } s ? new TranscriptFacts(s.Hashes, s.Minutes) : TranscriptFacts.Empty;

    /// <summary>
    /// Whether the transcript contains <paramref name="text"/> in the entries written between <paramref name="from"/> and
    /// <paramref name="to"/>. Only that stretch of the file is read, found through the byte offset recorded for each minute,
    /// so asking about a commit's subject costs a few hundred KB, not the whole transcript.
    /// </summary>
    public static bool MentionsBetween(string path, string text, DateTimeOffset from, DateTimeOffset to)
    {
        if (text.Length == 0 || Scan(path) is not { } scanned) return false;
        var offsets = scanned.MinuteOffsets;
        if (offsets.Count == 0) return false;
        var fromMinute = from.ToUnixTimeSeconds() / 60;
        var toMinute = to.ToUnixTimeSeconds() / 60;
        // From the first entry written in the window to the first written after it.
        var start = -1L;
        var end = scanned.Length;
        foreach (var (minute, offset) in offsets)
        {
            if (minute < fromMinute) continue;
            if (minute > toMinute) { end = offset; break; }
            if (start < 0) start = offset;
        }
        if (start < 0 || end <= start) return false;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var length = (int)Math.Min(end - start, 64 * 1024 * 1024);
            stream.Seek(start, SeekOrigin.Begin);
            var buffer = new byte[length];
            var read = stream.Read(buffer, 0, length);
            return buffer.AsSpan(0, read).IndexOf(Encoding.UTF8.GetBytes(text)) >= 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static Scanned? Scan(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (Cache.TryGetValue(path, out var cached) && cached.Length == stream.Length) return cached;
            var resume = cached is not null && cached.Length < stream.Length;
            var scanned = resume ? cached!.Length : 0L;
            var hashes = resume ? new HashSet<string>(cached!.Hashes, StringComparer.Ordinal) : new HashSet<string>(StringComparer.Ordinal);
            var minutes = resume ? new HashSet<long>(cached!.Minutes) : new HashSet<long>();
            var offsets = resume ? new SortedList<long, long>(cached!.MinuteOffsets) : new SortedList<long, long>();

            // Re-read a little before the old end, so a hash or timestamp split across two scans is still found whole.
            // Starting mid-file, the first run may be the tail of a longer word, so it only counts after a separator.
            var from = Math.Max(0, scanned - 64);
            stream.Seek(from, SeekOrigin.Begin);
            var buffer = new byte[ChunkBytes];
            var run = new StringBuilder(40);
            var stamp = new StringBuilder(40);
            var keyAt = 0;          // how much of "timestamp":" has matched so far
            var inStamp = false;    // reading the timestamp value
            var stampAt = 0L;       // where the entry holding it begins, near enough: the key's position
            var position = from;    // absolute offset of buffer[0]
            int read;
            var boundary = from == 0; // the byte before the run is not a letter or digit
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                for (var i = 0; i < read; i++)
                {
                    var b = buffer[i];

                    if (inStamp)
                    {
                        if (b == (byte)'"' || stamp.Length >= 40)
                        {
                            inStamp = false;
                            if (DateTimeOffset.TryParse(stamp.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t))
                            {
                                var minute = t.ToUnixTimeSeconds() / 60;
                                minutes.Add(minute);
                                if (!offsets.ContainsKey(minute)) offsets[minute] = stampAt;
                            }
                            stamp.Clear();
                        }
                        else stamp.Append((char)b);
                    }
                    else if (b == TimestampKey[keyAt])
                    {
                        if (keyAt == 0) stampAt = position + i;
                        if (++keyAt == TimestampKey.Length) { keyAt = 0; inStamp = true; }
                    }
                    else if (b == TimestampKey[0]) { keyAt = 1; stampAt = position + i; }
                    else keyAt = 0;

                    var hex = b is >= (byte)'0' and <= (byte)'9' or >= (byte)'a' and <= (byte)'f';
                    if (hex)
                    {
                        if (run.Length == 0 && !boundary) continue;
                        if (run.Length < 41) run.Append((char)b);
                        continue;
                    }
                    var word = b is >= (byte)'g' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z' || b == (byte)'_';
                    if (run.Length is >= 7 and <= 40 && !word) hashes.Add(run.ToString(0, 7));
                    run.Clear();
                    boundary = !word && !(b is >= (byte)'0' and <= (byte)'9');
                }
                position += read;
            }
            if (run.Length is >= 7 and <= 40) hashes.Add(run.ToString(0, 7));
            var result = new Scanned(stream.Length, hashes, minutes, offsets);
            Cache[path] = result;
            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
