namespace AgentHub.Services.Instructions;

/// <summary>
/// The context helper (🦀 in the context window): the user says in their own words what to add or change in an
/// instruction file, and Claude Code replies with targeted edits in the same shape as <see cref="InstructionReview"/>,
/// so they are shown and applied the same way. Same safety as Review: print mode on the user's own login, all tools off,
/// no settings or CLAUDE.md of the user's own, so it only reads the text it is sent and never edits files.
/// </summary>
public static class InstructionRequest
{
    /// <summary>Longest request sent; a pasted wall of text is cut here rather than refused.</summary>
    public const int MaxAskLength = 8000;

    public const string SystemPrompt = """
        You edit instruction files that coding agent CLIs (Claude Code, Codex, Gemini) load at the start of every session,
        such as CLAUDE.md, AGENTS.md and GEMINI.md. The user tells you, in their own words, what they want added or changed.
        Do exactly that, as edits to the file:
        - turn the request into clear, specific rules an agent can follow, saying when they apply;
        - put each rule in the section where it belongs; add a new "## " section only when none fits, in the file's style;
        - match the file's voice, formatting and level of detail; keep it short, no preamble or generic advice;
        - if the file already says it, tighten or extend that rule instead of adding a duplicate;
        - if the request contradicts a rule, change that rule rather than leaving both;
        - keep everything else as it is. Never invent facts about the user's projects, tools, paths or people.
        If the request is unclear, too broad to act on, or not something an instruction file should hold, return no edits
        and say why in the summary, with a suggestion of what to ask instead. If it belongs better in the other kind of
        file (user-wide rather than a repository's, or the reverse), still make the edit but say so in the summary.

        Each edit replaces one passage of whole lines, and passages never overlap. Name the passage by its ends only,
        never copying the lines in between: "start" is its first line copied character for character (add the next line
        too if needed, so that it occurs only once in the file); "end" is its last line copied the same way, or "" when
        the passage is just "start". "replace" is the complete new text for the whole passage, so to add lines after a
        line, give that line as "start" and as the first line of "replace". To add at the end of the file, give an empty
        "start". "why" is one short line: what changes. "summary" is one or two sentences to the user: what you did.
        """;

    /// <summary>The message sent on stdin: the file as Review sends it, then the user's request.</summary>
    public static string BuildRequest(InstructionFile file, string text, string? repoFolder, string ask)
    {
        var trimmed = ask.Trim();
        if (trimmed.Length > MaxAskLength) trimmed = trimmed[..MaxAskLength];
        return $"""
            {InstructionReview.BuildRequest(file, text, repoFolder)}

            <request>
            {trimmed.Replace("\r\n", "\n")}
            </request>
            """;
    }

    /// <summary>Runs the request. Throws <see cref="OperationCanceledException"/> when cancelled, <see cref="InvalidOperationException"/> on failure.</summary>
    public static async Task<ReviewSuggestion> RunAsync(ProcessRunner runner, string claudeCommand, InstructionFile file, string text,
        string? repoFolder, string ask, CancellationToken cancellationToken)
    {
        using var reply = await ClaudePrint.RunAsync(runner, claudeCommand, SystemPrompt, InstructionReview.ReplySchema,
            BuildRequest(file, text, repoFolder, ask), cancellationToken);
        return InstructionReview.ReadSuggestion(reply.RootElement);
    }
}
