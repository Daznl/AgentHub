using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentHub.Services.World;

/// <summary>
/// One thing a session did, read from a line of its transcript: a tool call (read, edit, a command…), a prompt or a
/// reply. <see cref="Path"/> is the file the action touched as the transcript gives it (absolute for Claude Code,
/// often relative to the session's folder for Codex).
/// </summary>
public sealed record SessionAction(DateTimeOffset Time, string Kind, string? Path, string? Text);

/// <summary>
/// Turns Claude Code and Codex transcript lines into <see cref="SessionAction"/>s for the 3D world's crabs. Field names
/// follow <see cref="History.SessionHistoryReader"/>'s parsers. Unknown lines give nothing; nothing here throws on bad input.
/// </summary>
public static partial class TranscriptActions
{
    public static List<SessionAction> ParseClaudeLine(string line)
    {
        var actions = new List<SessionAction>();
        if (!TryParse(line, out var root)) return actions;
        using (root)
        {
            var r = root.RootElement;
            var type = Str(r, "type");
            var time = Time(r);
            if (!r.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object) return actions;
            message.TryGetProperty("content", out var content);
            if (type == "assistant" && content.ValueKind == JsonValueKind.Array)
            {
                foreach (var block in content.EnumerateArray())
                {
                    var blockType = Str(block, "type");
                    if (blockType == "tool_use")
                    {
                        block.TryGetProperty("input", out var input);
                        actions.Add(ClassifyClaudeTool(time, Str(block, "name") ?? "", input));
                    }
                    else if (blockType == "text" && Str(block, "text") is { Length: > 0 } text)
                        actions.Add(new SessionAction(time, "reply", null, WorldSnapshotBuilder.Shorten(text, 70)));
                }
            }
            else if (type == "user" && !Bool(r, "isMeta") && !Bool(r, "isCompactSummary"))
            {
                // A prompt is plain text from the owner; a tool result comes back as a user line too and is not one
                string? text = content.ValueKind == JsonValueKind.String ? content.GetString() : null;
                if (content.ValueKind == JsonValueKind.Array)
                {
                    if (content.EnumerateArray().Any(b => Str(b, "type") == "tool_result")) return actions;
                    text = string.Join(" ", content.EnumerateArray().Where(b => Str(b, "type") == "text").Select(b => Str(b, "text")));
                }
                if (!string.IsNullOrWhiteSpace(text) && !text.TrimStart().StartsWith('<'))
                    actions.Add(new SessionAction(time, "prompt", null, WorldSnapshotBuilder.Shorten(text, 70)));
            }
        }
        return actions;
    }

    public static List<SessionAction> ParseCodexLine(string line)
    {
        var actions = new List<SessionAction>();
        if (!TryParse(line, out var root)) return actions;
        using (root)
        {
            var r = root.RootElement;
            var time = Time(r);
            if (!r.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object) return actions;
            var type = Str(r, "type");
            var payloadType = Str(payload, "type");
            if (type == "event_msg" && payloadType == "user_message" && Str(payload, "message") is { Length: > 0 } prompt)
                actions.Add(new SessionAction(time, "prompt", null, WorldSnapshotBuilder.Shorten(prompt, 70)));
            else if (type == "response_item")
            {
                switch (payloadType)
                {
                    case "message" when Str(payload, "role") == "assistant":
                        var text = payload.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array
                            ? string.Join(" ", content.EnumerateArray().Select(b => Str(b, "text")).Where(t => t is not null))
                            : null;
                        if (!string.IsNullOrWhiteSpace(text)) actions.Add(new SessionAction(time, "reply", null, WorldSnapshotBuilder.Shorten(text, 70)));
                        break;
                    case "function_call" or "custom_tool_call":
                        var name = Str(payload, "name") ?? "";
                        var args = Str(payload, "arguments") ?? Str(payload, "input") ?? "";
                        actions.AddRange(ClassifyCodexTool(time, name, args));
                        break;
                    case "local_shell_call":
                        if (payload.TryGetProperty("action", out var action) && action.TryGetProperty("command", out var command))
                            actions.Add(Command(time, Join(command)));
                        break;
                    case "web_search_call":
                        actions.Add(new SessionAction(time, "web", null, payload.TryGetProperty("action", out var a) ? Str(a, "query") : null));
                        break;
                }
            }
        }
        return actions;
    }

    public static SessionAction ClassifyClaudeTool(DateTimeOffset time, string name, JsonElement input)
    {
        string? In(string key) => input.ValueKind == JsonValueKind.Object ? Str(input, key) : null;
        return name switch
        {
            "Read" => new SessionAction(time, "read", In("file_path"), "Read " + FileName(In("file_path"))),
            "Edit" or "MultiEdit" => new SessionAction(time, "edit", In("file_path"), "Edit " + FileName(In("file_path"))),
            "NotebookEdit" => new SessionAction(time, "edit", In("notebook_path"), "Edit " + FileName(In("notebook_path"))),
            "Write" => new SessionAction(time, "write", In("file_path"), "Write " + FileName(In("file_path"))),
            "Grep" or "Glob" or "LS" => new SessionAction(time, "search", In("path"), $"{name} {In("pattern")}".Trim()),
            "Bash" or "PowerShell" => Command(time, In("command") ?? ""),
            "WebFetch" or "WebSearch" => new SessionAction(time, "web", null, In("url") ?? In("query")),
            "Task" or "Agent" => new SessionAction(time, "agent", null, In("description") ?? "sub-agent"),
            _ when name.StartsWith("mcp__", StringComparison.Ordinal) => McpTool(time, name),
            _ => new SessionAction(time, "tool", null, name)
        };
    }

    private static IEnumerable<SessionAction> ClassifyCodexTool(DateTimeOffset time, string name, string args)
    {
        if (name is "apply_patch")
        {
            var patch = args;
            if (TryParse(args, out var doc)) using (doc) patch = Str(doc.RootElement, "input") ?? args;
            var any = false;
            foreach (Match m in PatchFile().Matches(patch))
            {
                any = true;
                yield return new SessionAction(time, m.Groups[1].Value == "Add" ? "write" : "edit", m.Groups[2].Value.Trim(),
                    (m.Groups[1].Value == "Add" ? "Write " : "Edit ") + FileName(m.Groups[2].Value.Trim()));
            }
            if (!any) yield return new SessionAction(time, "edit", null, "apply_patch");
            yield break;
        }
        if (name is "shell" or "shell_command" or "exec_command" or "container.exec")
        {
            var command = args;
            if (TryParse(args, out var doc))
                using (doc)
                {
                    var r = doc.RootElement;
                    if (r.ValueKind == JsonValueKind.Object && (r.TryGetProperty("command", out var c) || r.TryGetProperty("cmd", out c)))
                        command = c.ValueKind == JsonValueKind.Array ? Join(c) : c.GetString() ?? args;
                }
            yield return Command(time, command);
            yield break;
        }
        yield return name.StartsWith("mcp__", StringComparison.Ordinal) ? McpTool(time, name) : new SessionAction(time, "tool", null, name);
    }

    private static SessionAction McpTool(DateTimeOffset time, string name)
    {
        var lower = name.ToLowerInvariant();
        var kind = lower.Contains("snowflake") ? "snowflake"
            : lower.Contains("github") ? "pr"
            : lower.Contains("azure") || lower.Contains("microsoft") ? "azure"
            : "tool";
        return new SessionAction(time, kind, null, name["mcp__".Length..].Replace("__", " · "));
    }

    /// <summary>What a shell command does, as far as the world cares: git steps, Snowflake, Azure, the web, builds and tests.</summary>
    public static SessionAction Command(DateTimeOffset time, string command)
    {
        var c = command.Trim();
        var kind =
            GitCommit().IsMatch(c) ? "commit" :
            GitPush().IsMatch(c) ? "push" :
            GitMerge().IsMatch(c) ? "merge" :
            GhPr().IsMatch(c) ? "pr" :
            GitBranch().IsMatch(c) ? "branch" :
            Snowflake().IsMatch(c) ? "snowflake" :
            Azure().IsMatch(c) ? "azure" :
            Web().IsMatch(c) ? "web" :
            Test().IsMatch(c) ? "test" :
            Build().IsMatch(c) ? "build" :
            "bash";
        return new SessionAction(time, kind, null, WorldSnapshotBuilder.Shorten(c, 70));
    }

    [GeneratedRegex(@"\bgit\b[^|;&\n]*\bcommit\b", RegexOptions.IgnoreCase)] private static partial Regex GitCommit();
    [GeneratedRegex(@"\bgit\b[^|;&\n]*\bpush\b", RegexOptions.IgnoreCase)] private static partial Regex GitPush();
    [GeneratedRegex(@"\bgit\b[^|;&\n]*\b(merge|pull)\b|\bgh\s+pr\s+merge\b", RegexOptions.IgnoreCase)] private static partial Regex GitMerge();
    [GeneratedRegex(@"\bgh\s+(pr|api)\b", RegexOptions.IgnoreCase)] private static partial Regex GhPr();
    [GeneratedRegex(@"\bgit\b[^|;&\n]*\b(checkout|switch|worktree|branch)\b", RegexOptions.IgnoreCase)] private static partial Regex GitBranch();
    [GeneratedRegex(@"\bsnow\s|\bsnowsql\b|snowflake", RegexOptions.IgnoreCase)] private static partial Regex Snowflake();
    [GeneratedRegex(@"(^|[\s;&|(])(az|azd)\s|\bbicep\b|\b(New|Set|Get|Remove|Connect)-Az\w*", RegexOptions.IgnoreCase)] private static partial Regex Azure();
    [GeneratedRegex(@"\bcurl\b|\bwget\b|Invoke-(WebRequest|RestMethod)", RegexOptions.IgnoreCase)] private static partial Regex Web();
    [GeneratedRegex(@"\b(dotnet|npm|pnpm|yarn)\s+test\b|\bpytest\b|\bvitest\b|\bjest\b|res://tests/", RegexOptions.IgnoreCase)] private static partial Regex Test();
    [GeneratedRegex(@"\b(dotnet|msbuild)\s+(build|publish|restore)\b|\b(npm|pnpm|yarn)\s+(run\s+)?build\b|\bgodot\b", RegexOptions.IgnoreCase)] private static partial Regex Build();
    [GeneratedRegex(@"^\*\*\* (Update|Add|Delete) File: (.+)$", RegexOptions.Multiline)] private static partial Regex PatchFile();

    private static string FileName(string? path) =>
        string.IsNullOrWhiteSpace(path) ? "" : path.Replace('\\', '/').TrimEnd('/').Split('/')[^1];

    private static string Join(JsonElement array) =>
        array.ValueKind == JsonValueKind.Array
            ? string.Join(" ", array.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : e.ToString()))
            : array.ToString();

    private static bool TryParse(string text, out JsonDocument doc)
    {
        doc = null!;
        if (string.IsNullOrWhiteSpace(text)) return false;
        try { doc = JsonDocument.Parse(text); return true; }
        catch (JsonException) { return false; }
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static DateTimeOffset Time(JsonElement r) =>
        Str(r, "timestamp") is { } s && DateTimeOffset.TryParse(s, out var t) ? t : DateTimeOffset.UtcNow;
}

/// <summary>
/// Reads only what was appended to transcripts since the last call, whole lines only (a half-written last line waits
/// for the next call). A file that got shorter was replaced and is read again from its start. Opened shared, so the CLI
/// writing it is never blocked.
/// </summary>
public sealed class TranscriptTail
{
    private readonly Dictionary<string, long> _offsets = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// New complete lines. A file seen for the first time starts <paramref name="backlog"/> bytes before its end (at the
    /// next line start), so a newly seen session shows its last few actions rather than its whole history.
    /// </summary>
    public List<string> Read(string path, long backlog = 16 * 1024)
    {
        var lines = new List<string>();
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var length = stream.Length;
            var first = !_offsets.TryGetValue(path, out var offset);
            if (first) offset = Math.Max(0, length - backlog);
            if (offset > length) offset = 0;
            if (offset == length) { _offsets[path] = offset; return lines; }
            stream.Seek(offset, SeekOrigin.Begin);
            var buffer = new byte[length - offset];
            var read = 0;
            while (read < buffer.Length)
            {
                var n = stream.Read(buffer, read, buffer.Length - read);
                if (n == 0) break;
                read += n;
            }
            var lastNewline = Array.LastIndexOf(buffer, (byte)'\n', read - 1);
            if (lastNewline < 0) { if (first) _offsets[path] = offset; return lines; }
            var start = 0;
            if (first && offset > 0)
            {
                // Started mid-file: skip to the first whole line
                var firstNewline = Array.IndexOf(buffer, (byte)'\n', 0, lastNewline + 1);
                start = firstNewline + 1;
            }
            var text = Encoding.UTF8.GetString(buffer, start, lastNewline + 1 - start);
            foreach (var line in text.Split('\n'))
                if (line.Trim().Length > 0) lines.Add(line.TrimEnd('\r'));
            _offsets[path] = offset + lastNewline + 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return lines;
    }

    public void Forget(string path) => _offsets.Remove(path);
}
