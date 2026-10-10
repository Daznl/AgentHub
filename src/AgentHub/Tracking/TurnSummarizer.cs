using System.Text;
using System.Text.RegularExpressions;
using AgentHub.Services;

namespace AgentHub.Tracking;

/// <summary>A finished turn boiled down: what happened, and the message the user would most likely send next.</summary>
public sealed record TurnInsight(string? Summary, string? Suggestion);

/// <summary>
/// Turns an agent's full closing reply into a short summary and a suggested next message, in one
/// stripped-down headless Claude Code call (<c>claude -p</c>): no tools, hooks, MCP servers, skills or
/// project CLAUDE.md, and nothing saved as a session. ANTHROPIC_API_KEY is removed from that call's
/// environment so it signs in with the user's claude.ai login (subscription) instead of billing an API
/// key. AgentHub never handles a credential itself. The suggestion is only shown to the user; it is
/// never sent to the session.
/// </summary>
public sealed partial class TurnSummarizer
{
    public const int MaxWords = 20;

    // The full closing reply is sent, cut only if it is very long.
    private const int MaxReplyChars = 12_000;
    private const int MaxPromptChars = 2_000;
    private const int MaxHistoryItemChars = 300;

    private const string SystemPrompt =
        "You track a coding session for a busy user who is not watching it. You get recent turns for context, " +
        "the user's latest message and the assistant's full closing reply. Output exactly two lines and nothing else:\n" +
        "SUMMARY: at most 20 words on what the assistant did or found, past tense, no subject " +
        "(for example: Added login page; tests pass).\n" +
        "NEXT: at most 20 words: the message the user would most likely send next, written as the user talking to the assistant.\n" +
        "Rules for NEXT: if the reply asks a question or offers to do something, answer it (for example: Yes, start building it). " +
        "If it offers a choice, pick the option the reply recommends. If work is finished and working, write \"Test it: <how, " +
        "from the reply>, then <the next step>\", taking the next step from the recent turns when the plan shows one. " +
        "Start with Yes only when answering a question. If something failed or is blocked, ask for the fix. If the user " +
        "must do something themselves, tell them what to run or check.\n" +
        "No preamble, markdown or quotes.";

    // Variables that would route the call to an API key or a third-party provider instead of the claude.ai login.
    private static readonly string[] NonSubscriptionAuth = ["ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN"];

    private readonly ProcessRunner _runner;
    private readonly string _model;

    public TurnSummarizer(ProcessRunner runner, string model = "haiku")
    {
        _runner = runner;
        _model = string.IsNullOrWhiteSpace(model) ? "haiku" : model;
    }

    /// <summary>Arguments for the headless call. Public for tests.</summary>
    public IReadOnlyList<string> BuildArguments() =>
    [
        "-p",
        "--model", _model,
        "--tools", "",
        "--no-session-persistence",
        "--strict-mcp-config",
        "--disable-slash-commands",
        "--settings", "{\"disableAllHooks\":true}",
        "--system-prompt", SystemPrompt
    ];

    /// <summary>
    /// What the call reads on stdin: earlier turns as prompt + summary (oldest first), then the latest
    /// prompt and the assistant's full closing reply. Public for tests.
    /// </summary>
    public static string BuildInput(string userMessage, string reply, IEnumerable<JournalEntry> recentTurns)
    {
        var sb = new StringBuilder();
        var history = recentTurns.ToList();
        if (history.Count > 0)
        {
            sb.AppendLine("Recent turns (oldest first):");
            var n = 1;
            foreach (var turn in history)
            {
                var result = turn.Summary ?? turn.FinalText;
                sb.AppendLine($"{n++}. User: {Clip(Flatten(turn.UserMessage), MaxHistoryItemChars)}");
                sb.AppendLine($"   Result: {Clip(Flatten(result), MaxHistoryItemChars)}");
            }
            sb.AppendLine();
        }
        sb.AppendLine("Latest user message:");
        sb.AppendLine(Clip(userMessage.Trim(), MaxPromptChars));
        sb.AppendLine();
        sb.AppendLine("Assistant's full closing reply:");
        sb.AppendLine(Clip(reply.Trim(), MaxReplyChars));
        return sb.ToString();
    }

    /// <summary>Returns the summary and suggestion for a turn, or null if the call failed.</summary>
    public async Task<TurnInsight?> AnalyzeAsync(string claudeExecutable, string userMessage, string reply,
        IEnumerable<JournalEntry> recentTurns, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(reply)) return null;

        var (exitCode, stdout, _) = await _runner.RunAsync(
            claudeExecutable,
            BuildArguments(),
            WorkingFolder(),
            cancellationToken,
            TimeSpan.FromSeconds(90),
            standardInput: BuildInput(userMessage, reply, recentTurns),
            removeEnvironmentVariables: NonSubscriptionAuth);

        if (exitCode != 0) return null;
        var insight = ParseOutput(stdout);
        return insight.Summary is null && insight.Suggestion is null ? null : insight;
    }

    /// <summary>
    /// Reads the SUMMARY and NEXT lines, each capped at <see cref="MaxWords"/>. Unlabelled output is taken as
    /// the summary. Public for tests.
    /// </summary>
    public static TurnInsight ParseOutput(string output)
    {
        string? summary = null, suggestion = null;
        var unlabelled = new List<string>();
        foreach (var raw in output.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim().Trim('*').Trim();
            if (line.Length == 0) continue;
            var label = LabelRegex().Match(line);
            if (!label.Success) { unlabelled.Add(line); continue; }

            var text = Flatten(line[label.Length..]);
            if (text.Length == 0) continue;
            if (label.Groups[1].Value.Equals("SUMMARY", StringComparison.OrdinalIgnoreCase)) summary ??= text;
            else suggestion ??= text;
        }
        if (summary is null && unlabelled.Count > 0) summary = Flatten(string.Join(' ', unlabelled));

        return new TurnInsight(
            summary is { Length: > 0 } ? LimitWords(summary, MaxWords) : null,
            suggestion is { Length: > 0 } ? LimitWords(suggestion, MaxWords) : null);
    }

    // An empty folder of AgentHub's own, so no repository CLAUDE.md is picked up.
    private static string WorkingFolder()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentHub", "summarizer");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static int CountWords(string text) => WhitespaceRegex().Split(text.Trim()).Count(w => w.Length > 0);

    /// <summary>Keeps the first <paramref name="max"/> words, marking a cut with an ellipsis.</summary>
    public static string LimitWords(string text, int max)
    {
        var words = WhitespaceRegex().Split(text.Trim()).Where(w => w.Length > 0).ToArray();
        return words.Length <= max ? string.Join(' ', words) : string.Join(' ', words.Take(max)).TrimEnd(',', ';', ':', '.') + "…";
    }

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..max] + "…";

    // One line, light markdown removed (headings, bullets, bold/code markers), surrounding quotes dropped.
    private static string Flatten(string text)
    {
        var lines = text.Replace("\r", "").Split('\n')
            .Select(l => MarkdownLinePrefixRegex().Replace(l, "").Trim())
            .Where(l => l.Length > 0);
        var flat = string.Join(' ', lines).Replace("**", "").Replace("`", "");
        return WhitespaceRegex().Replace(flat, " ").Trim().Trim('"');
    }

    [GeneratedRegex(@"^(SUMMARY|NEXT)\s*:\s*", RegexOptions.IgnoreCase)]
    private static partial Regex LabelRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"^\s*(#{1,6}\s+|[-*+]\s+|\d+\.\s+|>\s*)")]
    private static partial Regex MarkdownLinePrefixRegex();
}
