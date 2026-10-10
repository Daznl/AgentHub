using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentHub.Tracking;

/// <summary>
/// Turns Claude Code's session transcript (~/.claude/projects/&lt;folder&gt;/&lt;session&gt;.jsonl) into
/// prompt/response turns, one line at a time. Only the user's prompt and the agent's closing text
/// are kept; tool calls, tool results, thinking and attachments are skipped, so a turn stays small
/// however much work happened inside it.
/// </summary>
public sealed partial class ClaudeTranscriptParser
{
    // Tools whose input names a file the agent changed.
    private static readonly HashSet<string> FileEditingTools = new(StringComparer.Ordinal)
    {
        "Edit", "MultiEdit", "Write", "NotebookEdit"
    };

    private TranscriptTurn? _current;
    private TranscriptTurn? _lastCompleted;
    private bool _currentHasAssistantOutput;
    private bool _lastAssistantWasText;
    private bool _endTurnSeen;
    private string _pendingText = "";

    /// <summary>The turn the agent is working on, or null when it is waiting for the user.</summary>
    public TranscriptTurn? Current => _current;

    public IReadOnlyList<TurnEvent> Feed(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return [];

        JsonDocument doc;
        try { doc = JsonDocument.Parse(line); }
        catch (JsonException) { return []; }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return [];
            if (GetBool(root, "isSidechain") || GetBool(root, "isMeta")) return [];

            return GetString(root, "type") switch
            {
                "user" => OnUser(root),
                "assistant" => OnAssistant(root),
                _ => []
            };
        }
    }

    /// <summary>
    /// Completes the open turn if the agent's last output was text, for transcripts that never record
    /// an end_turn stop reason. Call when the session goes quiet or exits.
    /// </summary>
    public IReadOnlyList<TurnEvent> Settle()
    {
        if (_current is null || !_currentHasAssistantOutput || !(_lastAssistantWasText || _endTurnSeen)) return [];
        return [Complete(TurnStatus.Complete, null)];
    }

    private IReadOnlyList<TurnEvent> OnUser(JsonElement root)
    {
        if (!TryGetPrompt(root, out var prompt)) return [];

        var events = new List<TurnEvent>();
        if (_current is not null)
        {
            // A prompt with no agent output in between (e.g. /model) is not worth a turn.
            if (_currentHasAssistantOutput)
                events.Add(Complete(TurnStatus.Interrupted, null));
            else
                _current = null;
        }

        _current = new TranscriptTurn
        {
            TurnId = GetString(root, "uuid") ?? Guid.NewGuid().ToString(),
            SessionId = GetString(root, "sessionId"),
            UserMessage = prompt,
            StartedAt = GetTimestamp(root)
        };
        _lastCompleted = null;
        _currentHasAssistantOutput = false;
        _lastAssistantWasText = false;
        _endTurnSeen = false;
        _pendingText = "";
        events.Add(new TurnEvent(TurnEventKind.Started, _current));
        return events;
    }

    private IReadOnlyList<TurnEvent> OnAssistant(JsonElement root)
    {
        if (_current is null)
        {
            // The agent carried on after finishing (a hook or background task woke it): reopen that turn.
            if (_lastCompleted is null) return [];
            _current = _lastCompleted;
            _current.Status = TurnStatus.Working;
            _lastCompleted = null;
            _pendingText = "";
        }

        if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object) return [];

        if (message.TryGetProperty("content", out var content))
        {
            if (content.ValueKind == JsonValueKind.String)
            {
                AppendText(content.GetString());
            }
            else if (content.ValueKind == JsonValueKind.Array)
            {
                foreach (var block in content.EnumerateArray())
                {
                    switch (GetString(block, "type"))
                    {
                        case "text":
                            AppendText(GetString(block, "text"));
                            break;
                        case "tool_use":
                            OnToolUse(block);
                            break;
                    }
                }
            }
        }

        // One reply is written as one line per content block, each carrying the reply's stop reason.
        // Finish on the text block; a thinking-only line waits for the text that follows it.
        _endTurnSeen = GetString(message, "stop_reason") is "end_turn" or "stop_sequence";
        if (_endTurnSeen && _lastAssistantWasText && LineHasText(message))
            return [Complete(TurnStatus.Complete, GetTimestamp(root))];

        return [];
    }

    private static bool LineHasText(JsonElement message) =>
        message.TryGetProperty("content", out var content)
        && (content.ValueKind == JsonValueKind.String
            || (content.ValueKind == JsonValueKind.Array
                && content.EnumerateArray().Any(b => GetString(b, "type") == "text" && !string.IsNullOrWhiteSpace(GetString(b, "text")))));

    private void AppendText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        _pendingText = _pendingText.Length == 0 ? text.Trim() : _pendingText + "\n\n" + text.Trim();
        _currentHasAssistantOutput = true;
        _lastAssistantWasText = true;
    }

    private void OnToolUse(JsonElement block)
    {
        var turn = _current!;
        turn.ToolCalls++;
        _currentHasAssistantOutput = true;
        _lastAssistantWasText = false;
        // Text written before a tool call is narration, not the closing summary.
        _pendingText = "";

        if (FileEditingTools.Contains(GetString(block, "name") ?? "")
            && block.TryGetProperty("input", out var input) && input.ValueKind == JsonValueKind.Object)
        {
            var path = GetString(input, "file_path") ?? GetString(input, "notebook_path");
            if (!string.IsNullOrWhiteSpace(path) && !turn.FilesTouched.Contains(path, StringComparer.OrdinalIgnoreCase))
                turn.FilesTouched.Add(path);
        }
    }

    private TurnEvent Complete(TurnStatus status, DateTimeOffset? endedAt)
    {
        var turn = _current!;
        turn.Status = status;
        turn.FinalText = _pendingText;
        turn.EndedAt = endedAt ?? DateTimeOffset.Now;
        _lastCompleted = status == TurnStatus.Complete ? turn : null;
        _endTurnSeen = false;
        _current = null;
        return new TurnEvent(TurnEventKind.Completed, turn);
    }

    // A real prompt typed (or pasted) by the user, as opposed to tool results, command output or
    // messages the harness injects. Returns the prompt with injected system reminders removed.
    private static bool TryGetPrompt(JsonElement root, out string prompt)
    {
        prompt = "";
        if (root.TryGetProperty("toolUseResult", out _)) return false;
        if (root.TryGetProperty("origin", out var origin) && origin.ValueKind == JsonValueKind.Object
            && GetString(origin, "kind") is { } kind && kind != "human")
            return false;
        if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object) return false;
        if (!message.TryGetProperty("content", out var content)) return false;

        string text;
        if (content.ValueKind == JsonValueKind.String)
        {
            text = content.GetString() ?? "";
        }
        else if (content.ValueKind == JsonValueKind.Array)
        {
            var parts = new List<string>();
            foreach (var block in content.EnumerateArray())
            {
                var blockType = GetString(block, "type");
                if (blockType == "tool_result") return false;
                if (blockType == "text" && GetString(block, "text") is { } t) parts.Add(t);
            }
            text = string.Join("\n", parts);
        }
        else
        {
            return false;
        }

        text = SystemReminderRegex().Replace(text, "").Trim();
        if (text.Length == 0) return false;
        if (text.StartsWith("<local-command-", StringComparison.Ordinal)) return false;

        // Slash commands are recorded as tags; keep them as the "/name args" the user typed.
        var command = CommandNameRegex().Match(text);
        if (command.Success)
        {
            var args = CommandArgsRegex().Match(text);
            text = args.Success && args.Groups[1].Value.Trim().Length > 0
                ? $"{command.Groups[1].Value.Trim()} {args.Groups[1].Value.Trim()}"
                : command.Groups[1].Value.Trim();
        }

        prompt = text;
        return true;
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool GetBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static DateTimeOffset? GetTimestamp(JsonElement root) =>
        DateTimeOffset.TryParse(GetString(root, "timestamp"), out var ts) ? ts : null;

    [GeneratedRegex(@"<system-reminder>.*?</system-reminder>", RegexOptions.Singleline)]
    private static partial Regex SystemReminderRegex();

    [GeneratedRegex(@"<command-name>(.*?)</command-name>", RegexOptions.Singleline)]
    private static partial Regex CommandNameRegex();

    [GeneratedRegex(@"<command-args>(.*?)</command-args>", RegexOptions.Singleline)]
    private static partial Regex CommandArgsRegex();
}
