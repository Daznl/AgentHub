using System.ComponentModel;
using System.IO;
using System.Text.Json;

namespace AgentHub.Services.Instructions;

/// <summary>What the reviewer suggested: one line per change, and the whole file with every change made.</summary>
public sealed record ReviewSuggestion(IReadOnlyList<string> Changes, string RevisedText);

/// <summary>
/// Asks Claude Code, in print mode on the user's own login, to review an instruction file. No API key: the request goes
/// through the installed CLI. All tools are off, so it can only read the text it is sent and reply; it never edits files.
/// </summary>
public static class InstructionReview
{
    /// <summary>Where the CLI runs: an empty folder, so no repository's own CLAUDE.md or settings are picked up.</summary>
    public const string WorkingDirectory = @"C:\data\agenthub\review";

    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

    public const string SystemPrompt = """
        You review instruction files that coding agent CLIs (Claude Code, Codex, Gemini) load at the start of every session,
        such as CLAUDE.md, AGENTS.md and GEMINI.md. Suggest changes that make an agent follow the file better:
        - remove contradictions and duplicated rules;
        - make vague rules specific and actionable, and clear about when they apply;
        - tighten wording that spends context without adding meaning;
        - keep personal standing preferences in the user-wide file and project facts in a repository's file;
        - fix obvious errors in the text (broken Markdown, typos that change meaning).
        Keep the author's intent, voice, structure and every rule they clearly want. Never invent facts about their
        projects, tools, paths or people. Do not add generic advice an agent follows anyway. Prefer few, high-value changes;
        if the file is already good, return it unchanged and say so.

        Reply in exactly this format and nothing else:
        <changes>
        - one line per change: what changed and why
        </changes>
        <revised>
        the complete revised file, every line, exactly as it should be saved, not wrapped in a code fence
        </revised>
        """;

    /// <summary>Print mode, JSON envelope, no tools, no saved session, no MCP servers, our system prompt.</summary>
    public static IReadOnlyList<string> Arguments() =>
        ["-p", "--output-format", "json", "--tools", "", "--no-session-persistence", "--strict-mcp-config", "--system-prompt", SystemPrompt];

    /// <summary>The message sent on stdin: what the file is, where it applies, and its text.</summary>
    public static string BuildRequest(InstructionFile file, string text, string? repoFolder)
    {
        var scope = repoFolder is null
            ? "User-wide file: the CLI loads it in every folder, before any repository's own file."
            : $"Repository file for {repoFolder}: loaded only for sessions in that repository.";
        var body = string.IsNullOrWhiteSpace(text)
            ? "The file is empty. Suggest a short skeleton of headings with one-line placeholders the author fills in; do not guess their content."
            : "Review this file.";
        return $"""
            File: {file.Label} ({file.Path})
            For: {CliName(file.Cli)}
            {scope}
            {body}

            <file>
            {text}
            </file>
            """;
    }

    /// <summary>Runs the review. Throws <see cref="OperationCanceledException"/> when cancelled, <see cref="InvalidOperationException"/> on failure.</summary>
    public static async Task<ReviewSuggestion> RunAsync(ProcessRunner runner, string claudeCommand, InstructionFile file, string text,
        string? repoFolder, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(WorkingDirectory);
        (int ExitCode, string StdOut, string StdErr) run;
        try
        {
            run = await runner.RunAsync(claudeCommand, Arguments(), WorkingDirectory, cancellationToken, Timeout,
                standardInput: BuildRequest(file, text, repoFolder));
        }
        catch (Win32Exception)
        {
            throw new InvalidOperationException(
                $"Claude Code ({claudeCommand}) was not found. Install it, or set its command under Settings → Agents.");
        }
        cancellationToken.ThrowIfCancellationRequested();

        var reply = ExtractResult(run.StdOut);
        if (reply is null)
        {
            var detail = string.IsNullOrWhiteSpace(run.StdErr) ? run.StdOut : run.StdErr;
            throw new InvalidOperationException($"Claude Code did not return a review (exit code {run.ExitCode}).\n{Trim(detail)}");
        }
        return AlignTrailingNewline(text, Parse(reply));
    }

    /// <summary>
    /// The reply text from Claude Code's JSON envelope; null when stdout is not one. Throws when the CLI reports an
    /// error (not signed in, usage limit), with its message.
    /// </summary>
    public static string? ExtractResult(string stdout)
    {
        var json = stdout.Trim();
        if (!json.StartsWith('{')) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var result = root.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
            if (root.TryGetProperty("is_error", out var e) && e.ValueKind == JsonValueKind.True)
                throw new InvalidOperationException($"Claude Code reported an error: {Trim(result ?? json)}");
            return result;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Reads the &lt;changes&gt; and &lt;revised&gt; blocks. Throws <see cref="InvalidOperationException"/> without a revised file.</summary>
    public static ReviewSuggestion Parse(string reply)
    {
        var revised = Between(reply, "<revised>", "</revised>")
                      ?? throw new InvalidOperationException($"The review came back in an unexpected form, so nothing was changed.\n{Trim(reply)}");
        revised = StripFence(TrimOneNewline(revised));

        var changes = (Between(reply, "<changes>", "</changes>") ?? "")
            .Split('\n')
            .Select(l => l.Trim().TrimStart('-', '*', '•').Trim())
            .Where(l => l.Length > 0)
            .ToList();
        return new ReviewSuggestion(changes, LineDiff.JoinLines(LineDiff.SplitLines(revised)));
    }

    /// <summary>Gives the revised text the original's ending (with or without a final newline), so that alone never shows as a change.</summary>
    public static ReviewSuggestion AlignTrailingNewline(string original, ReviewSuggestion suggestion)
    {
        var body = suggestion.RevisedText.TrimEnd('\r', '\n');
        var ending = original.EndsWith('\n') || original.EndsWith('\r') ? "\r\n" : "";
        return suggestion with { RevisedText = body + ending };
    }

    // Text after the first opening tag, up to the last closing tag (or the end, when the reply was cut off).
    private static string? Between(string text, string open, string close)
    {
        var start = text.IndexOf(open, StringComparison.Ordinal);
        if (start < 0) return null;
        start += open.Length;
        var end = text.LastIndexOf(close, StringComparison.Ordinal);
        return end >= start ? text[start..end] : text[start..];
    }

    private static string TrimOneNewline(string text)
    {
        if (text.StartsWith("\r\n")) text = text[2..];
        else if (text.StartsWith('\n')) text = text[1..];
        if (text.EndsWith("\r\n")) text = text[..^2];
        else if (text.EndsWith('\n')) text = text[..^1];
        return text;
    }

    // A whole file wrapped in ``` despite the instructions.
    private static string StripFence(string text)
    {
        var lines = LineDiff.SplitLines(text);
        if (lines.Length >= 2 && lines[0].TrimStart().StartsWith("```") && lines[^1].Trim() == "```")
            return LineDiff.JoinLines(lines[1..^1]);
        return text;
    }

    private static string Trim(string text) => text.Length > 600 ? text[..600].TrimEnd() + "…" : text.Trim();

    public static string CliName(InstructionCli cli) => cli switch
    {
        InstructionCli.ClaudeCode => "Claude Code",
        InstructionCli.Codex => "Codex",
        _ => "Gemini / Antigravity"
    };
}
