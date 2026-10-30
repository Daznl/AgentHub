using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using AgentHub.Services.Context;

namespace AgentHub.Services.History;

/// <summary>What one entry in a session's history is.</summary>
public enum HistoryKind
{
    /// <summary>Something the user typed.</summary>
    Prompt,
    /// <summary>Text the agent wrote back.</summary>
    Reply,
    /// <summary>The agent's visible reasoning (Claude thinking, Codex reasoning summary).</summary>
    Thinking,
    /// <summary>A tool the agent called, with its input.</summary>
    ToolCall,
    /// <summary>What a tool returned.</summary>
    ToolResult,
    /// <summary>Slash commands, interrupts, compaction and other session events.</summary>
    Event
}

/// <summary>One message in a session, in transcript order.</summary>
public sealed record HistoryEntry(
    HistoryKind Kind,
    string Text,
    DateTimeOffset? Timestamp,
    string? ToolName = null,
    bool IsError = false,
    bool IsSubAgent = false);

/// <summary>One coding CLI session that ran in a folder, as found on disk.</summary>
public sealed record SessionSummary(
    string FilePath,
    ContextSource Agent,
    string? Title,
    string? FirstPrompt,
    DateTimeOffset StartedAt,
    DateTimeOffset LastActivity,
    long SizeBytes);

/// <summary>
/// Reads the complete history of coding sessions from the transcripts the CLIs already write (the same files
/// <see cref="SessionContextReader"/> uses), so nothing is scraped from the terminal and sessions started outside
/// AgentHub are included. Claude Code: <c>~/.claude/projects/&lt;encoded cwd&gt;/*.jsonl</c>. Codex:
/// <c>~/.codex/sessions/yyyy/mm/dd/rollout-*.jsonl</c>, matched on the <c>session_meta</c> cwd.
/// Reads are incremental: <see cref="ReadEntries"/> returns the byte offset to continue from, so a live session is
/// followed without re-parsing a transcript that can reach hundreds of MB.
/// </summary>
public static class SessionHistoryReader
{
    /// <summary>Longest text kept for one entry. Tool output (file dumps, base64) can be megabytes.</summary>
    public const int MaxEntryChars = 20_000;

    /// <summary>How much of each end of a transcript is read to summarise it.</summary>
    private const int SummaryProbeBytes = 256 * 1024;

    private const int ChunkBytes = 1024 * 1024;

    // A rollout's first line never changes, so its cwd is read once per file.
    private static readonly ConcurrentDictionary<string, string?> CodexCwdCache = new(StringComparer.OrdinalIgnoreCase);

    // Summaries keyed by path, reused while the file's size and write time are unchanged, so re-listing is cheap.
    private static readonly ConcurrentDictionary<string, (long Length, DateTime WriteTime, SessionSummary? Summary)> SummaryCache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every Claude Code and Codex session recorded for <paramref name="workingDirectory"/>, newest first.</summary>
    public static IReadOnlyList<SessionSummary> ListSessions(string workingDirectory,
        string? claudeProjectsDir = null, string? codexSessionsDir = null)
    {
        var sessions = new List<SessionSummary>();
        foreach (var path in FindClaudeSessions(workingDirectory, claudeProjectsDir))
            if (CachedSummary(path, ContextSource.ClaudeCode) is { } s) sessions.Add(s);
        foreach (var path in FindCodexSessions(workingDirectory, codexSessionsDir))
            if (CachedSummary(path, ContextSource.Codex) is { } s) sessions.Add(s);
        return sessions.OrderByDescending(s => s.LastActivity).ToList();
    }

    private static SessionSummary? CachedSummary(string path, ContextSource agent)
    {
        try
        {
            var info = new FileInfo(path);
            if (SummaryCache.TryGetValue(path, out var cached) &&
                cached.Length == info.Length && cached.WriteTime == info.LastWriteTimeUtc)
                return cached.Summary;
            var summary = Summarise(path, agent);
            SummaryCache[path] = (info.Length, info.LastWriteTimeUtc, summary);
            return summary;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static IEnumerable<string> FindClaudeSessions(string workingDirectory, string? projectsDir)
    {
        projectsDir ??= SessionContextReader.ClaudeProjectsDir;
        var encoded = SessionContextReader.EncodeClaudeProjectName(workingDirectory);
        // Claude Code shortens very long folder names and appends a hash, so match on the leading part as a fallback.
        var pattern = encoded.Length > 200 ? encoded[..200] + "*" : encoded;
        try
        {
            if (!Directory.Exists(projectsDir)) return [];
            // Top level only: sub-agent transcripts live in <session>/subagents/.
            return Directory.EnumerateDirectories(projectsDir, pattern)
                .SelectMany(dir => Directory.EnumerateFiles(dir, "*.jsonl", SearchOption.TopDirectoryOnly))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static IEnumerable<string> FindCodexSessions(string workingDirectory, string? sessionsDir)
    {
        sessionsDir ??= SessionContextReader.CodexSessionsDir;
        var target = NormaliseDir(workingDirectory);
        try
        {
            if (!Directory.Exists(sessionsDir)) return [];
            return Directory.EnumerateFiles(sessionsDir, "rollout-*.jsonl", SearchOption.AllDirectories)
                .Where(f => string.Equals(CodexCwdCache.GetOrAdd(f, ReadCodexCwd), target, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// Title, first prompt and time span of a session, from the head and tail of its transcript only.
    /// Null when the file has no conversation in it (e.g. Claude Code opened and closed without a prompt).
    /// </summary>
    public static SessionSummary? Summarise(string path, ContextSource agent)
    {
        FileInfo info;
        try
        {
            info = new FileInfo(path);
            if (!info.Exists) return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var head = ReadProbe(path, fromEnd: false);
        DateTimeOffset? started = null;
        string? firstPrompt = null;
        foreach (var line in head)
        {
            if (!TryParse(line, out var root)) continue;
            started ??= ReadTimestamp(root);
            if (firstPrompt is null)
            {
                var entries = agent == ContextSource.ClaudeCode ? ParseClaudeLine(root) : ParseCodexLine(root);
                firstPrompt = entries.FirstOrDefault(e => e.Kind == HistoryKind.Prompt)?.Text;
            }
            if (started is not null && firstPrompt is not null) break;
        }

        string? title = null;
        if (agent == ContextSource.ClaudeCode)
        {
            // Claude Code re-writes its generated title throughout the session; the newest one wins.
            foreach (var line in ReadProbe(path, fromEnd: true).AsEnumerable().Reverse())
            {
                if (!line.Contains("\"ai-title\"", StringComparison.Ordinal) &&
                    !line.Contains("\"type\":\"summary\"", StringComparison.Ordinal)) continue;
                if (!TryParse(line, out var root)) continue;
                title = GetString(root, "aiTitle") ?? GetString(root, "summary");
                if (title is not null) break;
            }
        }

        if (firstPrompt is null && title is null) return null;

        var last = new DateTimeOffset(info.LastWriteTimeUtc);
        return new SessionSummary(path, agent, title, firstPrompt is null ? null : Truncate(firstPrompt, 500),
            started ?? last, last, info.Length);
    }

    /// <summary>
    /// Entries in complete lines written after byte <paramref name="offset"/>, and the offset to continue from.
    /// A trailing line without its newline is still being written and is left for the next call.
    /// </summary>
    public static (List<HistoryEntry> Entries, long NextOffset) ReadEntries(string path, ContextSource agent, long offset = 0)
    {
        var entries = new List<HistoryEntry>();
        var next = offset;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (offset > stream.Length) offset = 0; // file was replaced
            stream.Seek(offset, SeekOrigin.Begin);
            next = offset;

            var buffer = new byte[ChunkBytes];
            var pending = new MemoryStream();
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                var start = 0;
                for (var i = 0; i < read; i++)
                {
                    if (buffer[i] != (byte)'\n') continue;
                    pending.Write(buffer, start, i - start);
                    next += pending.Length + 1;
                    ParseLineInto(pending, agent, entries);
                    pending.SetLength(0);
                    start = i + 1;
                }
                pending.Write(buffer, start, read - start);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Partial result; the caller retries from the returned offset.
        }
        return (entries, next);
    }

    private static void ParseLineInto(MemoryStream line, ContextSource agent, List<HistoryEntry> entries)
    {
        if (line.Length == 0) return;
        try
        {
            using var doc = JsonDocument.Parse(line.GetBuffer().AsMemory(0, (int)line.Length));
            entries.AddRange(agent == ContextSource.ClaudeCode ? ParseClaudeLine(doc.RootElement) : ParseCodexLine(doc.RootElement));
        }
        catch (JsonException) { /* not JSON; skip */ }
    }

    // ---- Claude Code ----

    /// <summary>
    /// One transcript line as zero or more entries. Claude Code writes one content block per line; bookkeeping lines
    /// (attachments, snapshots, API request copies, titles) produce nothing.
    /// </summary>
    public static IEnumerable<HistoryEntry> ParseClaudeLine(JsonElement root)
    {
        var type = GetString(root, "type");
        var ts = ReadTimestamp(root);
        var sub = root.TryGetProperty("isSidechain", out var sc) && sc.ValueKind == JsonValueKind.True;

        if (type == "system")
        {
            if (GetString(root, "subtype") == "compact_boundary")
                yield return new HistoryEntry(HistoryKind.Event, "Conversation compacted", ts, IsSubAgent: sub);
            yield break;
        }
        if (type is not ("user" or "assistant")) yield break;
        if (!root.TryGetProperty("message", out var message) || !message.TryGetProperty("content", out var content)) yield break;

        if (type == "user")
        {
            if (root.TryGetProperty("isCompactSummary", out var cs) && cs.ValueKind == JsonValueKind.True)
            {
                yield return new HistoryEntry(HistoryKind.Event, "Conversation compacted. Summary carried forward:\n\n" +
                    Truncate(TextOf(content)), ts, IsSubAgent: sub);
                yield break;
            }
            // Image size notes and similar companions to a real prompt.
            if (root.TryGetProperty("isMeta", out var meta) && meta.ValueKind == JsonValueKind.True) yield break;

            // Newer CLIs tag typed prompts with origin.kind = "human"; anything else (task notifications) is an event.
            var human = !root.TryGetProperty("origin", out var origin) ||
                        (origin.TryGetProperty("kind", out var kind) && kind.GetString() == "human");

            if (content.ValueKind == JsonValueKind.String)
            {
                if (ClassifyUserText(content.GetString()!, human, ts, sub) is { } e) yield return e;
                yield break;
            }
            if (content.ValueKind != JsonValueKind.Array) yield break;

            var images = 0;
            foreach (var block in content.EnumerateArray())
            {
                switch (GetString(block, "type"))
                {
                    case "text":
                        if (ClassifyUserText(GetString(block, "text") ?? "", human, ts, sub) is { } e) yield return e;
                        break;
                    case "image":
                        images++;
                        break;
                    case "tool_result":
                        var isError = block.TryGetProperty("is_error", out var err) && err.ValueKind == JsonValueKind.True;
                        var output = block.TryGetProperty("content", out var c) ? TextOf(c) : "";
                        yield return new HistoryEntry(HistoryKind.ToolResult, Truncate(output.TrimEnd()), ts,
                            IsError: isError, IsSubAgent: sub);
                        break;
                }
            }
            if (images > 0)
                yield return new HistoryEntry(HistoryKind.Prompt, images == 1 ? "[image attached]" : $"[{images} images attached]", ts, IsSubAgent: sub);
            yield break;
        }

        // assistant
        if (content.ValueKind == JsonValueKind.String)
        {
            var text = content.GetString()!.Trim();
            if (text.Length > 0) yield return new HistoryEntry(HistoryKind.Reply, Truncate(text), ts, IsSubAgent: sub);
            yield break;
        }
        if (content.ValueKind != JsonValueKind.Array) yield break;

        foreach (var block in content.EnumerateArray())
        {
            switch (GetString(block, "type"))
            {
                case "text":
                    var text = (GetString(block, "text") ?? "").Trim();
                    if (text.Length > 0) yield return new HistoryEntry(HistoryKind.Reply, Truncate(text), ts, IsSubAgent: sub);
                    break;
                case "thinking":
                    // Empty when the reasoning is encrypted (only a signature is stored).
                    var thinking = (GetString(block, "thinking") ?? "").Trim();
                    if (thinking.Length > 0) yield return new HistoryEntry(HistoryKind.Thinking, Truncate(thinking), ts, IsSubAgent: sub);
                    break;
                case "tool_use":
                    var name = GetString(block, "name") ?? "tool";
                    var input = block.TryGetProperty("input", out var i) ? DescribeToolInput(i) : "";
                    yield return new HistoryEntry(HistoryKind.ToolCall, Truncate(input), ts, ToolName: name, IsSubAgent: sub);
                    break;
            }
        }
    }

    // Typed prompts become Prompt; slash/bang command echoes, interrupts and notifications become Event.
    private static HistoryEntry? ClassifyUserText(string raw, bool human, DateTimeOffset? ts, bool sub)
    {
        var text = raw.Trim();
        if (text.Length == 0) return null;

        if (text.StartsWith("[Request interrupted", StringComparison.Ordinal))
            return new HistoryEntry(HistoryKind.Event, "Interrupted by user", ts, IsSubAgent: sub);

        if (text.StartsWith('<'))
        {
            var command = ExtractTag(text, "command-name");
            if (command is not null)
            {
                var args = ExtractTag(text, "command-args");
                return new HistoryEntry(HistoryKind.Prompt, string.IsNullOrWhiteSpace(args) ? command : $"{command} {args}", ts, IsSubAgent: sub);
            }
            var bash = ExtractTag(text, "bash-input");
            if (bash is not null) return new HistoryEntry(HistoryKind.Prompt, "! " + bash, ts, IsSubAgent: sub);

            var stdout = ExtractTag(text, "local-command-stdout") ?? ExtractTag(text, "bash-stdout");
            var stderr = ExtractTag(text, "bash-stderr");
            if (stdout is not null || stderr is not null)
            {
                var output = string.Join("\n", new[] { stdout, stderr }.Where(s => !string.IsNullOrWhiteSpace(s))).Trim();
                return output.Length == 0 ? null : new HistoryEntry(HistoryKind.ToolResult, Truncate(output), ts,
                    ToolName: "command output", IsError: !string.IsNullOrWhiteSpace(stderr), IsSubAgent: sub);
            }
            // Caveats and other wrappers the CLI injects.
            if (text.StartsWith("<local-command-caveat>", StringComparison.Ordinal)) return null;
            return new HistoryEntry(HistoryKind.Event, Truncate(text), ts, IsSubAgent: sub);
        }

        return new HistoryEntry(human ? HistoryKind.Prompt : HistoryKind.Event, Truncate(text), ts, IsSubAgent: sub);
    }

    private static string? ExtractTag(string text, string tag)
    {
        var open = $"<{tag}>";
        var start = text.IndexOf(open, StringComparison.Ordinal);
        if (start < 0) return null;
        start += open.Length;
        var end = text.IndexOf($"</{tag}>", start, StringComparison.Ordinal);
        return (end < 0 ? text[start..] : text[start..end]).Trim();
    }

    // ---- Codex ----

    /// <summary>
    /// One rollout line as zero or more entries. Prompts come from <c>user_message</c> events (the text as typed, without
    /// the environment context Codex injects into the model's user turn); replies, reasoning and tools from response items.
    /// </summary>
    public static IEnumerable<HistoryEntry> ParseCodexLine(JsonElement root)
    {
        var ts = ReadTimestamp(root);
        if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object) yield break;
        var payloadType = GetString(payload, "type");

        switch (GetString(root, "type"))
        {
            case "event_msg":
                if (payloadType == "user_message")
                {
                    var text = (GetString(payload, "message") ?? "").Trim();
                    if (text.Length > 0) yield return new HistoryEntry(HistoryKind.Prompt, Truncate(text), ts);
                }
                else if (payloadType == "turn_aborted")
                    yield return new HistoryEntry(HistoryKind.Event, "Interrupted by user", ts);
                yield break;

            case "compacted":
                yield return new HistoryEntry(HistoryKind.Event, "Conversation compacted", ts);
                yield break;

            case "response_item":
                break;

            default:
                yield break;
        }

        switch (payloadType)
        {
            case "message" when GetString(payload, "role") == "assistant":
                var reply = payload.TryGetProperty("content", out var content) ? TextOf(content).Trim() : "";
                if (reply.Length > 0) yield return new HistoryEntry(HistoryKind.Reply, Truncate(reply), ts);
                break;

            case "reasoning":
                var summary = payload.TryGetProperty("summary", out var s) ? TextOf(s).Trim() : "";
                if (summary.Length > 0) yield return new HistoryEntry(HistoryKind.Thinking, Truncate(summary), ts);
                break;

            case "function_call":
            case "custom_tool_call":
                var name = GetString(payload, "name") ?? "tool";
                var args = GetString(payload, "arguments") ?? GetString(payload, "input") ?? "";
                yield return new HistoryEntry(HistoryKind.ToolCall, Truncate(DescribeToolArguments(args)), ts, ToolName: name);
                break;

            case "local_shell_call":
                var command = payload.TryGetProperty("action", out var action) && action.TryGetProperty("command", out var cmd)
                    ? cmd.ValueKind == JsonValueKind.Array
                        ? string.Join(" ", cmd.EnumerateArray().Select(a => a.GetString()))
                        : cmd.ToString()
                    : "";
                yield return new HistoryEntry(HistoryKind.ToolCall, Truncate(command), ts, ToolName: "shell");
                break;

            case "web_search_call":
                var query = payload.TryGetProperty("action", out var a) ? GetString(a, "query") : null;
                yield return new HistoryEntry(HistoryKind.ToolCall, query ?? "", ts, ToolName: "web_search");
                break;

            case "function_call_output":
            case "custom_tool_call_output":
                var output = payload.TryGetProperty("output", out var o) ? CodexOutputText(o) : "";
                yield return new HistoryEntry(HistoryKind.ToolResult, Truncate(output.TrimEnd()), ts);
                break;
        }
    }

    // Function output is a plain string, or a JSON string wrapping {"output": "...", "metadata": {...}}.
    private static string CodexOutputText(JsonElement output)
    {
        if (output.ValueKind != JsonValueKind.String) return TextOf(output);
        var text = output.GetString() ?? "";
        if (!text.StartsWith('{')) return text;
        try
        {
            using var doc = JsonDocument.Parse(text);
            return GetString(doc.RootElement, "output") ?? text;
        }
        catch (JsonException)
        {
            return text;
        }
    }

    private static string DescribeToolArguments(string arguments)
    {
        if (!arguments.TrimStart().StartsWith('{')) return arguments;
        try
        {
            using var doc = JsonDocument.Parse(arguments);
            return DescribeToolInput(doc.RootElement);
        }
        catch (JsonException)
        {
            return arguments;
        }
    }

    // ---- shared ----

    /// <summary>
    /// A tool input as readable text: the field that says what the call does (command, path, pattern, ...) first, then
    /// every other field. Long strings such as file contents are kept; <see cref="Truncate"/> caps the whole entry.
    /// </summary>
    public static string DescribeToolInput(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object) return input.ToString();

        string[] headlineKeys = ["command", "cmd", "file_path", "path", "pattern", "url", "query", "description", "prompt", "skill"];
        var sb = new StringBuilder();
        var headline = headlineKeys.FirstOrDefault(k => input.TryGetProperty(k, out _));
        if (headline is not null) sb.AppendLine(ValueText(input.GetProperty(headline)));

        foreach (var prop in input.EnumerateObject())
        {
            if (prop.Name == headline) continue;
            var value = ValueText(prop.Value);
            sb.Append(prop.Name).Append(':').Append(value.Contains('\n') ? "\n" : " ").AppendLine(value);
        }
        return sb.ToString().TrimEnd();
    }

    private static string ValueText(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.Array when value.EnumerateArray().All(v => v.ValueKind == JsonValueKind.String) =>
            string.Join(" ", value.EnumerateArray().Select(v => v.GetString())),
        _ => value.ToString()
    };

    /// <summary>Text from a string or an array of content blocks (text, output_text, summary_text, nested tool content).</summary>
    private static string TextOf(JsonElement content)
    {
        switch (content.ValueKind)
        {
            case JsonValueKind.String:
                return content.GetString() ?? "";
            case JsonValueKind.Array:
                var parts = new List<string>();
                foreach (var block in content.EnumerateArray())
                {
                    if (block.ValueKind == JsonValueKind.String) { parts.Add(block.GetString() ?? ""); continue; }
                    if (block.ValueKind != JsonValueKind.Object) continue;
                    if (GetString(block, "text") is { } t) parts.Add(t);
                    else if (GetString(block, "type") == "image") parts.Add("[image]");
                    else if (block.TryGetProperty("content", out var inner)) parts.Add(TextOf(inner));
                }
                return string.Join("\n", parts);
            default:
                return "";
        }
    }

    private static string Truncate(string text) =>
        text.Length <= MaxEntryChars
            ? text
            : text[..MaxEntryChars] + $"\n… {text.Length - MaxEntryChars:N0} more characters in the transcript file";

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "…";

    /// <summary>Complete lines from the first or last <see cref="SummaryProbeBytes"/> of a file, in file order.</summary>
    private static List<string> ReadProbe(string path, bool fromEnd)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var length = stream.Length;
            var offset = fromEnd ? Math.Max(0, length - SummaryProbeBytes) : 0;
            var count = (int)Math.Min(SummaryProbeBytes, length - offset);
            stream.Seek(offset, SeekOrigin.Begin);
            var bytes = new byte[count];
            stream.ReadExactly(bytes);
            var lines = Encoding.UTF8.GetString(bytes).Split('\n').ToList();
            // Drop the cut-off line at whichever edge is mid-file.
            if (fromEnd && offset > 0 && lines.Count > 0) lines.RemoveAt(0);
            if (!fromEnd && count < length && lines.Count > 0) lines.RemoveAt(lines.Count - 1);
            return lines.Where(l => l.Length > 0).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static bool TryParse(string line, out JsonElement root)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            root = doc.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            root = default;
            return false;
        }
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
            return doc.RootElement.TryGetProperty("payload", out var payload) && GetString(payload, "cwd") is { } cwd
                ? NormaliseDir(cwd)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return null;
        }
    }

    private static string? GetString(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;

    private static DateTimeOffset? ReadTimestamp(JsonElement root) =>
        GetString(root, "timestamp") is { } ts &&
        DateTimeOffset.TryParse(ts, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : null;

    private static string NormaliseDir(string dir) =>
        Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
