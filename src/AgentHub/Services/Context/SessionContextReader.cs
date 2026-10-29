using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AgentHub.Services.Context;

/// <summary>Which on-disk transcript format a Cockpit session writes, picked from its command line.</summary>
public enum ContextSource { None, ClaudeCode, Codex }

/// <summary>
/// How full a coding session's context window is, as last recorded by the CLI itself.
/// <see cref="WindowIsAssumed"/> is true when the CLI does not record its window size and the figure
/// comes from settings; the UI must say so rather than present it as exact.
/// </summary>
public sealed record ContextReading(
    long UsedTokens,
    long WindowTokens,
    bool WindowIsAssumed,
    string? Model,
    DateTimeOffset RecordedAt,
    string Source,
    string FilePath,
    bool CompactedSinceLastTurn = false)
{
    public double UsedFraction => WindowTokens <= 0 ? 0 : Math.Clamp((double)UsedTokens / WindowTokens, 0, 1);
}

/// <summary>
/// Reads per-session context usage from the transcripts coding CLIs already write, so nothing is scraped from the terminal:
/// <list type="bullet">
/// <item>Claude Code: <c>~/.claude/projects/&lt;encoded cwd&gt;/&lt;session&gt;.jsonl</c>. Each assistant turn carries the
/// API <c>usage</c> block; prompt size = <c>input_tokens + cache_creation_input_tokens + cache_read_input_tokens</c>.
/// The window size is not recorded, so it is taken from settings.</item>
/// <item>Codex: <c>~/.codex/sessions/yyyy/mm/dd/rollout-*.jsonl</c>. <c>token_count</c> events carry
/// <c>last_token_usage.total_tokens</c> and <c>model_context_window</c>; the first line's <c>session_meta</c> holds the cwd.</item>
/// </list>
/// </summary>
public static class SessionContextReader
{
    public const string ClaudeSourceLabel = "Claude Code session transcript";
    public const string CodexSourceLabel = "Codex session rollout";

    /// <summary>Only the end of a transcript is scanned first; Claude transcripts can reach hundreds of MB.</summary>
    private const int TailBytes = 1024 * 1024;

    /// <summary>Allowance for the CLI creating its file slightly before AgentHub records the session start.</summary>
    private static readonly TimeSpan StartSlack = TimeSpan.FromSeconds(5);

    public static string ClaudeProjectsDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");

    public static string CodexSessionsDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "sessions");

    /// <summary>Picks the transcript format from the executable at the start of a command line.</summary>
    public static ContextSource DetectSource(string commandLine)
    {
        var trimmed = commandLine.TrimStart();
        string exe;
        if (trimmed.StartsWith('"'))
        {
            var close = trimmed.IndexOf('"', 1);
            exe = close > 0 ? trimmed[1..close] : trimmed[1..];
        }
        else
        {
            var space = trimmed.IndexOf(' ');
            exe = space > 0 ? trimmed[..space] : trimmed;
        }

        return Path.GetFileNameWithoutExtension(exe).ToLowerInvariant() switch
        {
            "claude" => ContextSource.ClaudeCode,
            "codex" => ContextSource.Codex,
            _ => ContextSource.None
        };
    }

    /// <summary>Claude Code's project folder name: every character other than a letter or digit becomes '-'.</summary>
    public static string EncodeClaudeProjectName(string workingDirectory)
    {
        var path = workingDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var sb = new StringBuilder(path.Length);
        foreach (var ch in path)
            sb.Append(char.IsAsciiLetterOrDigit(ch) ? ch : '-');
        return sb.ToString();
    }

    /// <summary>
    /// Newest Claude transcript for <paramref name="workingDirectory"/> written since <paramref name="since"/>, skipping
    /// <paramref name="excluded"/> (files other Cockpit panes already own). Null when the session has not written one yet.
    /// </summary>
    public static string? FindClaudeTranscript(string workingDirectory, DateTimeOffset since,
        IReadOnlySet<string> excluded, string? projectsDir = null)
    {
        projectsDir ??= ClaudeProjectsDir;
        var encoded = EncodeClaudeProjectName(workingDirectory);
        // Claude Code shortens very long folder names and appends a hash, so match on the leading part as a fallback.
        var pattern = encoded.Length > 200 ? encoded[..200] + "*" : encoded;

        try
        {
            if (!Directory.Exists(projectsDir)) return null;
            return Directory.EnumerateDirectories(projectsDir, pattern)
                .SelectMany(dir => new DirectoryInfo(dir).EnumerateFiles("*.jsonl"))
                .Where(f => new DateTimeOffset(f.LastWriteTimeUtc) >= since - StartSlack)
                .Where(f => !excluded.Contains(f.FullName))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Select(f => f.FullName)
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Last main-thread (not sub-agent) assistant turn's prompt size. <paramref name="configuredWindow"/> is used as the
    /// window; if the session is already bigger, the window is raised to the next known size so the bar never overflows.
    /// </summary>
    public static ContextReading? ReadClaude(string transcriptPath, long configuredWindow)
    {
        string? lastUsageLine = null;
        var compactedAfter = false;

        foreach (var line in ReadLinesNewestFirst(transcriptPath))
        {
            if (line.Contains("\"compact_boundary\"", StringComparison.Ordinal) && lastUsageLine is null)
            {
                compactedAfter = true;
                continue;
            }
            if (!line.Contains("\"type\":\"assistant\"", StringComparison.Ordinal) ||
                !line.Contains("\"usage\"", StringComparison.Ordinal) ||
                line.Contains("\"isSidechain\":true", StringComparison.Ordinal))
                continue;
            lastUsageLine = line;
            break;
        }

        if (lastUsageLine is null) return null;

        try
        {
            using var doc = JsonDocument.Parse(lastUsageLine);
            var root = doc.RootElement;
            if (!root.TryGetProperty("message", out var message) ||
                !message.TryGetProperty("usage", out var usage))
                return null;

            var used = GetLong(usage, "input_tokens") +
                       GetLong(usage, "cache_creation_input_tokens") +
                       GetLong(usage, "cache_read_input_tokens");
            var model = message.TryGetProperty("model", out var m) ? m.GetString() : null;

            var window = configuredWindow > 0 ? configuredWindow : 200_000;
            if (model?.Contains("[1m]", StringComparison.OrdinalIgnoreCase) == true) window = Math.Max(window, 1_000_000);
            while (used > window) window = window < 1_000_000 ? 1_000_000 : window * 2;

            return new ContextReading(used, window, WindowIsAssumed: true, model,
                ReadTimestamp(root) ?? File.GetLastWriteTimeUtc(transcriptPath),
                ClaudeSourceLabel, transcriptPath, compactedAfter);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Newest Codex rollout whose <c>session_meta</c> cwd is <paramref name="workingDirectory"/> and which was written since
    /// <paramref name="since"/>. Only today's and yesterday's date folders are scanned (sessions crossing midnight).
    /// </summary>
    public static string? FindCodexRollout(string workingDirectory, DateTimeOffset since,
        IReadOnlySet<string> excluded, string? sessionsDir = null)
    {
        sessionsDir ??= CodexSessionsDir;
        var target = NormaliseDir(workingDirectory);

        try
        {
            if (!Directory.Exists(sessionsDir)) return null;
            var start = since.LocalDateTime.Date.AddDays(-1);
            var dayDirs = new List<string>();
            for (var day = start; day <= DateTime.Now.Date; day = day.AddDays(1))
            {
                var dir = Path.Combine(sessionsDir, day.ToString("yyyy", CultureInfo.InvariantCulture),
                    day.ToString("MM", CultureInfo.InvariantCulture), day.ToString("dd", CultureInfo.InvariantCulture));
                if (Directory.Exists(dir)) dayDirs.Add(dir);
            }

            return dayDirs
                .SelectMany(dir => new DirectoryInfo(dir).EnumerateFiles("rollout-*.jsonl"))
                .Where(f => new DateTimeOffset(f.LastWriteTimeUtc) >= since - StartSlack)
                .Where(f => !excluded.Contains(f.FullName))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Where(f => string.Equals(ReadCodexCwd(f.FullName), target, StringComparison.OrdinalIgnoreCase))
                .Select(f => f.FullName)
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Last <c>token_count</c> event's context size and the model's window, both as Codex recorded them.</summary>
    public static ContextReading? ReadCodex(string rolloutPath)
    {
        foreach (var line in ReadLinesNewestFirst(rolloutPath))
        {
            if (!line.Contains("\"token_count\"", StringComparison.Ordinal) ||
                !line.Contains("\"model_context_window\"", StringComparison.Ordinal))
                continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (!root.TryGetProperty("payload", out var payload) ||
                    !payload.TryGetProperty("info", out var info) || info.ValueKind != JsonValueKind.Object ||
                    !info.TryGetProperty("last_token_usage", out var last))
                    continue;

                var window = GetLong(info, "model_context_window");
                if (window <= 0) continue;

                return new ContextReading(GetLong(last, "total_tokens"), window, WindowIsAssumed: false, Model: null,
                    ReadTimestamp(root) ?? File.GetLastWriteTimeUtc(rolloutPath), CodexSourceLabel, rolloutPath);
            }
            catch (JsonException) { /* partial line mid-write */ }
        }
        return null;
    }

    private static string? ReadCodexCwd(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var first = reader.ReadLine();
            if (first is null) return null;
            using var doc = JsonDocument.Parse(first);
            return doc.RootElement.TryGetProperty("payload", out var payload) &&
                   payload.TryGetProperty("cwd", out var cwd) && cwd.ValueKind == JsonValueKind.String
                ? NormaliseDir(cwd.GetString()!)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Lines from the end of the file backwards: first the last <see cref="TailBytes"/>, then the whole file if the
    /// tail held nothing useful. A transcript is appended while being read, so the file is shared for write.
    /// </summary>
    private static IEnumerable<string> ReadLinesNewestFirst(string path)
    {
        string[] tailLines;
        long length;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            length = stream.Length;
            var offset = Math.Max(0, length - TailBytes);
            stream.Seek(offset, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var text = reader.ReadToEnd();
            tailLines = text.Split('\n');
            // The first line of a mid-file tail is almost always cut off.
            if (offset > 0) tailLines = tailLines[1..];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        for (var i = tailLines.Length - 1; i >= 0; i--)
            if (tailLines[i].Length > 0) yield return tailLines[i];

        if (length <= TailBytes) yield break;

        List<string> all;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            all = [];
            // Only the part before the tail is new; stop once we reach it.
            long consumed = 0;
            while (consumed < length - TailBytes && reader.ReadLine() is { } line)
            {
                all.Add(line);
                consumed += Encoding.UTF8.GetByteCount(line) + 1;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        for (var i = all.Count - 1; i >= 0; i--)
            if (all[i].Length > 0) yield return all[i];
    }

    private static long GetLong(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var prop) && prop.TryGetInt64(out var value) ? value : 0;

    private static DateTimeOffset? ReadTimestamp(JsonElement root) =>
        root.TryGetProperty("timestamp", out var ts) && ts.ValueKind == JsonValueKind.String &&
        DateTimeOffset.TryParse(ts.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : null;

    private static string NormaliseDir(string dir) =>
        Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
