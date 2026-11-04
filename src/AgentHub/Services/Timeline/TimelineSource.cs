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

    /// <summary>Every commit on every branch, newest first as git gives them.</summary>
    public static async Task<List<TimelineCommit>> ReadCommitsAsync(ProcessRunner runner, TimelineScope scope, CancellationToken cancellationToken = default)
    {
        if (!scope.IsGit) return [];
        var output = await RunGitAsync(runner, scope.Root,
            ["log", "--all", "--source", $"--format=%H{Sep}%P{Sep}%cI{Sep}%an{Sep}%s{Sep}%S"], cancellationToken) ?? "";
        return ParseLog(output);
    }

    public static List<TimelineCommit> ParseLog(string output)
    {
        var commits = new List<TimelineCommit>();
        foreach (var line in output.Split('\n'))
        {
            var f = line.TrimEnd('\r').Split(Sep);
            if (f.Length < 6 || f[0].Length < 7) continue;
            if (!DateTimeOffset.TryParse(f[2], CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)) continue;
            var parents = f[1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            commits.Add(new TimelineCommit(f[0], parents, time, f[3], f[4], BranchName(f[5])));
        }
        return commits;
    }

    // refs/heads/feat/x -> feat/x, refs/remotes/origin/main -> main; tags and stash keep their short form.
    private static string? BranchName(string source)
    {
        var s = source.Trim();
        if (s.Length == 0) return null;
        foreach (var prefix in new[] { "refs/heads/", "refs/remotes/origin/", "refs/remotes/", "refs/tags/", "refs/" })
            if (s.StartsWith(prefix, StringComparison.Ordinal)) return s[prefix.Length..];
        return s;
    }

    /// <summary>Claude Code and Codex sessions recorded in any of the scope's folders, live or archived, each once.</summary>
    public static List<TimelineSessionInput> ReadSessions(TimelineScope scope, string? archiveRoot)
    {
        return scope.Folders
            .SelectMany(folder => SessionHistoryReader.ListSessions(folder, archiveRoot: archiveRoot).Select(s => (Folder: folder, Summary: s)))
            .GroupBy(s => Path.GetFileName(s.Summary.FilePath), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.MaxBy(s => s.Summary.SizeBytes))
            .Select(s => new TimelineSessionInput(s.Summary, LastEntryTime(s.Summary.FilePath) ?? s.Summary.LastActivity, TranscriptHashes.Read(s.Summary.FilePath), s.Folder))
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

/// <summary>
/// The commit hash prefixes (first 7 hex characters of every 7-to-40 character hex word) a transcript mentions. Scans are
/// incremental: a growing live transcript is only read from where the last scan stopped.
/// </summary>
public static class TranscriptHashes
{
    private static readonly ConcurrentDictionary<string, (long Scanned, HashSet<string> Hashes)> Cache = new(StringComparer.OrdinalIgnoreCase);
    private const int ChunkBytes = 1024 * 1024;

    public static IReadOnlySet<string> Read(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (Cache.TryGetValue(path, out var cached) && cached.Scanned == stream.Length) return cached.Hashes;
            var resume = cached.Hashes is not null && cached.Scanned < stream.Length;
            var scanned = resume ? cached.Scanned : 0L;
            var hashes = resume ? new HashSet<string>(cached.Hashes!, StringComparer.Ordinal) : new HashSet<string>(StringComparer.Ordinal);

            // Re-read a little before the old end, so a hash split across two scans is still found whole. Starting
            // mid-file, the first run may be the tail of a longer word, so it only counts after a separator.
            var from = Math.Max(0, scanned - 64);
            stream.Seek(from, SeekOrigin.Begin);
            var buffer = new byte[ChunkBytes];
            var run = new StringBuilder(40);
            int read;
            var boundary = from == 0; // the byte before the run is not a letter or digit
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                for (var i = 0; i < read; i++)
                {
                    var b = buffer[i];
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
            }
            if (run.Length is >= 7 and <= 40) hashes.Add(run.ToString(0, 7));
            Cache[path] = (stream.Length, hashes);
            return hashes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new HashSet<string>();
        }
    }
}
