using System.Text.Json;

namespace AgentHub.Services.Instructions;

/// <summary>
/// One suggested edit: the passage of whole lines from the line(s) <see cref="Start"/> through the line(s) <see cref="End"/>
/// (empty: the passage is just <see cref="Start"/>) becomes <see cref="Replace"/>, and why. Naming only the ends keeps
/// the reply short when a long passage is condensed or removed.
/// </summary>
public sealed record ReviewEdit(string Why, string Start, string End, string Replace);

/// <summary>The reviewer's overall verdict and its edits.</summary>
public sealed record ReviewSuggestion(string Summary, IReadOnlyList<ReviewEdit> Edits);

/// <summary>
/// The edits placed on the file: <see cref="Segments"/> holds same runs and one change per placed edit (in file order),
/// <see cref="Reasons"/> the matching reason per change, <see cref="Unplaced"/> edits whose text was not found once in the file
/// or that overlapped an earlier one.
/// </summary>
public sealed record ReviewPlan(IReadOnlyList<DiffSegment> Segments, IReadOnlyList<string> Reasons, IReadOnlyList<ReviewEdit> Unplaced);

/// <summary>
/// Asks Claude Code, in print mode on the user's own login, to review an instruction file. No API key: the request goes
/// through the installed CLI. All tools are off, so it can only read the text it is sent and reply; it never edits files.
/// It replies with targeted edits rather than the whole file, so a large file reviews as fast as a small one.
/// </summary>
public static class InstructionReview
{
    /// <summary>Where the CLI runs: an empty folder, so no repository's own CLAUDE.md or settings are picked up.</summary>
    public const string WorkingDirectory = ClaudePrint.WorkingDirectory;

    /// <summary>Generous: a large file with many edits can take a few minutes, and the window shows the time running.</summary>
    public static readonly TimeSpan Timeout = ClaudePrint.Timeout;

    public const int MaxEdits = 20;

    public const string SystemPrompt = """
        You review instruction files that coding agent CLIs (Claude Code, Codex, Gemini) load at the start of every session,
        such as CLAUDE.md, AGENTS.md and GEMINI.md. Suggest edits that make an agent follow the file better:
        - remove contradictions and duplicated rules;
        - make vague rules specific and actionable, and clear about when they apply;
        - tighten wording that spends context without adding meaning;
        - keep personal standing preferences in the user-wide file and project facts in a repository's file;
        - fix obvious errors in the text (broken Markdown, typos that change meaning).
        Keep the author's intent, voice, structure and every rule they clearly want. Never invent facts about their
        projects, tools, paths or people. Do not add generic advice an agent follows anyway. Prefer few, high-value edits
        (at most 20); if the file is already good, return no edits and say so in the summary.

        Each edit replaces one passage of whole lines, and passages never overlap. Name the passage by its ends only,
        never copying the lines in between: "start" is its first line copied character for character (add the next line
        too if needed, so that it occurs only once in the file); "end" is its last line copied the same way, or "" when
        the passage is just "start". "replace" is the complete new text for the whole passage ("" deletes it). "why" is
        one short line: what changes and why. If the file is empty, give one edit with an empty "start" whose "replace"
        is a short skeleton of headings with one-line placeholders; do not guess content.
        """;

    /// <summary>The reply shape Claude Code is held to (<c>--json-schema</c>).</summary>
    public const string ReplySchema = """
        {"type":"object","additionalProperties":false,"required":["summary","edits"],"properties":{
          "summary":{"type":"string","description":"One or two sentences: overall verdict on the file."},
          "edits":{"type":"array","items":{"type":"object","additionalProperties":false,"required":["why","start","end","replace"],"properties":{
            "why":{"type":"string"},"start":{"type":"string"},"end":{"type":"string"},"replace":{"type":"string"}}}}}}
        """;

    /// <summary>
    /// Print mode, JSON envelope with schema-checked output, no tools, no MCP servers, no saved session, and no settings
    /// or CLAUDE.md of the user's own (they would leak into the suggestions), with our system prompt.
    /// </summary>
    public static IReadOnlyList<string> Arguments() => ClaudePrint.Arguments(SystemPrompt, ReplySchema);

    /// <summary>The message sent on stdin: what the file is, where it applies, and its text.</summary>
    public static string BuildRequest(InstructionFile file, string text, string? repoFolder)
    {
        var scope = repoFolder is null
            ? "User-wide file: the CLI loads it in every folder, before any repository's own file."
            : $"Repository file for {repoFolder}: loaded only for sessions in that repository.";
        return $"""
            File: {file.Label} ({file.Path})
            For: {CliName(file.Cli)}
            {scope}

            <file>
            {LineDiff.JoinLines(LineDiff.SplitLines(text)).Replace("\r\n", "\n")}
            </file>
            """;
    }

    /// <summary>Runs the review. Throws <see cref="OperationCanceledException"/> when cancelled, <see cref="InvalidOperationException"/> on failure.</summary>
    public static async Task<ReviewSuggestion> RunAsync(ProcessRunner runner, string claudeCommand, InstructionFile file, string text,
        string? repoFolder, CancellationToken cancellationToken)
    {
        using var reply = await ClaudePrint.RunAsync(runner, claudeCommand, SystemPrompt, ReplySchema, BuildRequest(file, text, repoFolder), cancellationToken);
        return ReadSuggestion(reply.RootElement);
    }

    /// <summary>
    /// Reads Claude Code's JSON envelope: the schema-checked <c>structured_output</c>, else a JSON <c>result</c>. Null when
    /// stdout is not an envelope. Throws when the CLI reports an error (not signed in, usage limit), with its message.
    /// </summary>
    public static ReviewSuggestion? ParseEnvelope(string stdout)
    {
        using var reply = ClaudePrint.ParseEnvelope(stdout);
        return reply is null ? null : ReadSuggestion(reply.RootElement);
    }

    /// <summary>Reads a schema-checked reply into a suggestion; shared with <see cref="InstructionRequest"/>.</summary>
    internal static ReviewSuggestion ReadSuggestion(JsonElement element)
    {
        var summary = element.TryGetProperty("summary", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() ?? "" : "";
        var edits = new List<ReviewEdit>();
        if (element.TryGetProperty("edits", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray().Take(MaxEdits))
            {
                string Field(string name) => item.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                edits.Add(new ReviewEdit(Field("why").Trim(), Field("start"), Field("end"), Field("replace")));
            }
        }
        return new ReviewSuggestion(summary.Trim(), edits);
    }

    /// <summary>
    /// Places each edit on the file. A passage runs from the one place its start lines occur through the first
    /// occurrence of its end lines after that. Lines are matched exactly, else ignoring indentation and trailing spaces,
    /// else (for a one-line start or end) as the only line containing that text. Edits that cannot be placed, or that
    /// overlap an earlier one, are returned in <see cref="ReviewPlan.Unplaced"/>.
    /// </summary>
    public static ReviewPlan Plan(string original, IReadOnlyList<ReviewEdit> edits)
    {
        var lines = LineDiff.SplitLines(original);
        var placed = new List<(int First, int Last, string[] NewLines, string Why)>();
        var unplaced = new List<ReviewEdit>();

        foreach (var edit in edits)
        {
            var range = Locate(lines, edit) is { } found ? TrimUnchanged(lines, found) : ((int First, int Last, string[] NewLines)?)null;
            if (range is { } same && same.First > same.Last && same.NewLines.Length == 0) continue; // changes nothing
            if (range is not { } r || placed.Any(p => r.First <= p.Last && p.First <= r.Last))
            {
                unplaced.Add(edit);
                continue;
            }
            placed.Add((r.First, r.Last, r.NewLines, edit.Why));
        }

        // A stable order: several appends share a position and must keep the order they were given in, matching Reasons;
        // an insertion goes before a change that starts on the same line.
        var ordered = placed.OrderBy(p => p.First).ThenBy(p => p.Last >= p.First).ToList();
        var segments = new List<DiffSegment>();
        var next = 0;
        foreach (var (first, last, newLines, _) in ordered)
        {
            if (first > next) segments.Add(Same(lines, next, first - 1));
            segments.Add(new DiffSegment(true, lines[first..(last + 1)], newLines, first + 1));
            next = last + 1;
        }
        if (next < lines.Length) segments.Add(Same(lines, next, lines.Length - 1));
        return new ReviewPlan(segments, ordered.Select(p => p.Why).ToList(), unplaced);
    }

    /// <summary>
    /// Drops lines an edit keeps as they are from both ends of its passage, so an addition anchored on an existing line
    /// ("start" repeated as the first line of "replace") shows as added lines only, not that line removed and re-added.
    /// </summary>
    private static (int First, int Last, string[] NewLines) TrimUnchanged(string[] lines, (int First, int Last, string[] NewLines) range)
    {
        var (first, last, add) = range;
        var head = 0;
        while (first + head <= last && head < add.Length && lines[first + head] == add[head]) head++;
        var tail = 0;
        while (last - tail >= first + head && tail < add.Length - head && lines[last - tail] == add[^(tail + 1)]) tail++;
        return (first + head, last - tail, add[head..(add.Length - tail)]);
    }

    private static DiffSegment Same(string[] lines, int first, int last)
    {
        var run = lines[first..(last + 1)];
        return new DiffSegment(false, run, run, first + 1);
    }

    private static string[] ToLines(string text)
    {
        var trimmed = TrimOneNewline(text.Replace("\r\n", "\n").Replace('\r', '\n'));
        return trimmed.Length == 0 ? [] : trimmed.Split('\n');
    }

    private static (int First, int Last, string[] NewLines)? Locate(string[] lines, ReviewEdit edit)
    {
        var start = ToLines(edit.Start);
        var end = ToLines(edit.End);
        var replace = ToLines(edit.Replace);

        if (start.Length == 0)
        {
            if (replace.Length == 0) return null;
            // An empty file is filled in whole; a file with text gets the lines appended, before a trailing empty line
            // (text ending in a newline splits into one) so the file keeps its final newline.
            if (!lines.Any(l => l.Trim().Length > 0)) return (0, lines.Length - 1, lines.Length > 1 ? [.. replace, ""] : replace);
            var at = lines.Length > 0 && lines[^1].Length == 0 ? lines.Length - 1 : lines.Length;
            return (at, at - 1, replace);
        }

        var starts = Find(lines, start, from: 0);
        if (starts.Count != 1) return null;
        var first = starts[0];
        var last = first + start.Length - 1;

        if (end.Length > 0)
        {
            // The first end at or after the start that does not finish inside it.
            var ends = Find(lines, end, from: first).Where(k => k + end.Length - 1 >= last).ToList();
            if (ends.Count == 0) return null;
            last = ends[0] + end.Length - 1;
        }
        return (first, last, replace);
    }

    // Every index where `want` occurs at or after `from`, at the strictest level that finds any.
    private static List<int> Find(string[] lines, string[] want, int from)
    {
        foreach (var level in new[] { MatchLevel.Exact, MatchLevel.Trimmed, MatchLevel.Contains })
        {
            if (level == MatchLevel.Contains && (want.Length != 1 || want[0].Trim().Length < 8)) break;
            var hits = new List<int>();
            for (var i = from; i + want.Length <= lines.Length; i++)
            {
                var match = true;
                for (var j = 0; j < want.Length && match; j++) match = Matches(lines[i + j], want[j], level);
                if (match) hits.Add(i);
            }
            if (hits.Count > 0) return hits;
        }
        return [];
    }

    private enum MatchLevel { Exact, Trimmed, Contains }

    private static bool Matches(string line, string want, MatchLevel level) => level switch
    {
        MatchLevel.Exact => line == want,
        MatchLevel.Trimmed => line.Trim() == want.Trim(),
        _ => line.Contains(want.Trim(), StringComparison.Ordinal)
    };

    private static string TrimOneNewline(string text)
    {
        if (text.StartsWith('\n')) text = text[1..];
        if (text.EndsWith('\n')) text = text[..^1];
        return text;
    }

    public static string CliName(InstructionCli cli) => cli switch
    {
        InstructionCli.ClaudeCode => "Claude Code",
        InstructionCli.Codex => "Codex",
        _ => "Gemini / Antigravity"
    };
}
