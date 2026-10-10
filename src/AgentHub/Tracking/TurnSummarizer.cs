using System.Text.RegularExpressions;
using AgentHub.Services;

namespace AgentHub.Tracking;

/// <summary>
/// Boils an agent's closing reply down to one short sentence by running a stripped-down headless
/// Claude Code call (<c>claude -p</c>): no tools, hooks, MCP servers, skills or project CLAUDE.md, and
/// nothing saved as a session. ANTHROPIC_API_KEY is removed from that call's environment so it signs
/// in with the user's claude.ai login (subscription) instead of billing an API key. AgentHub never
/// handles a credential itself.
/// </summary>
public sealed partial class TurnSummarizer
{
    public const int MaxWords = 20;

    // Only the closing reply is sent; anything longer is cut, since the summary is one sentence anyway.
    private const int MaxInputChars = 12_000;

    private const string SystemPrompt =
        "You write the status line for a coding assistant's reply. Output one plain sentence of at most " +
        "20 words saying what the assistant did or found, written as the assistant in past tense without " +
        "a subject (for example: \"Added login page; tests pass\"). Mention a question only if the reply " +
        "asks the user to decide something. No preamble, markdown or quotes.";

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
    /// Returns a summary of at most <see cref="MaxWords"/> words, or null if the call failed.
    /// A reply that is already short enough is returned as is, without a call.
    /// </summary>
    public async Task<string?> SummarizeAsync(string claudeExecutable, string reply, CancellationToken cancellationToken = default)
    {
        var flat = Flatten(reply);
        if (flat.Length == 0) return null;
        if (CountWords(flat) <= MaxWords) return flat;

        var input = reply.Length > MaxInputChars ? reply[..MaxInputChars] : reply;
        var (exitCode, stdout, _) = await _runner.RunAsync(
            claudeExecutable,
            BuildArguments(),
            WorkingFolder(),
            cancellationToken,
            TimeSpan.FromSeconds(90),
            standardInput: input,
            removeEnvironmentVariables: NonSubscriptionAuth);

        var summary = Flatten(stdout);
        return exitCode == 0 && summary.Length > 0 ? LimitWords(summary, MaxWords) : null;
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

    // One line, light markdown removed (headings, bullets, bold/code markers), surrounding quotes dropped.
    private static string Flatten(string text)
    {
        var lines = text.Replace("\r", "").Split('\n')
            .Select(l => MarkdownLinePrefixRegex().Replace(l, "").Trim())
            .Where(l => l.Length > 0);
        var flat = string.Join(' ', lines).Replace("**", "").Replace("`", "");
        return WhitespaceRegex().Replace(flat, " ").Trim().Trim('"');
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"^\s*(#{1,6}\s+|[-*+]\s+|\d+\.\s+|>\s*)")]
    private static partial Regex MarkdownLinePrefixRegex();
}
