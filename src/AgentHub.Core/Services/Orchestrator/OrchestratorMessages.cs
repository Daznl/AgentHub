namespace AgentHub.Services.Orchestrator;

/// <summary>Texts AgentHub itself types into a worker session, and the cuts applied to what the orchestrator is sent.</summary>
public static class OrchestratorMessages
{
    /// <summary>Longest worker reply passed on; the end of a reply carries the summary, so the middle is cut.</summary>
    public const int MaxWorkerReplyChars = 8000;
    public const int MaxHandoffChars = 8000;

    /// <summary>Sent to the worker by AgentHub (not the orchestrator) when a handoff is due.</summary>
    public const string HandoffPrompt =
        "[AGENTHUB] Time to hand off: this session ends after this turn and a fresh session continues from your notes. Stop starting new work. " +
        "Run the tests, commit, and rewrite .agenthub/handoff.md so a newcomer can continue: what was built, architecture notes, test status, " +
        "decisions made, and the next tasks in order. Then end your turn.";

    /// <summary>Shortens text to about <paramref name="max"/> characters, keeping the start (and the end, when <paramref name="keepTail"/>).</summary>
    public static string Cut(string text, int max, bool keepTail)
    {
        if (text.Length <= max) return text;
        if (!keepTail) return text[..max].TrimEnd() + "\n…(cut)";
        var head = max / 4;
        var tail = max - head;
        return text[..head].TrimEnd() + "\n…(middle cut)…\n" + text[^tail..].TrimStart();
    }

    /// <summary>A file-name-safe slug of <paramref name="text"/>: lower-case letters and digits, dashes between words, at most 40 characters.</summary>
    public static string Slug(string text)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var c in text.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c)) sb.Append(c);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        var slug = sb.ToString().Trim('-');
        if (slug.Length > 40) slug = slug[..40].TrimEnd('-');
        return slug.Length == 0 ? "orchestrator" : slug;
    }
}
