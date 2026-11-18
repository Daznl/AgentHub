using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentHub.Services;
using AgentHub.Services.Instructions;
using Xunit;
using Xunit.Abstractions;

namespace AgentHub.Tests;

public class InstructionReviewTests(ITestOutputHelper output)
{
    private static readonly InstructionFile File = new(InstructionCli.ClaudeCode, "CLAUDE.md", @"C:\Users\me\.claude\CLAUDE.md", "");
    private const string Rules = "# Rules\r\n- be concise\r\n- use tabs\r\n- write tests\r\n";

    private static ReviewEdit Edit(string start, string replace, string why = "why", string end = "") => new(why, start, end, replace);

    private static string ApplyAll(string original, params ReviewEdit[] edits) =>
        LineDiff.Merge(InstructionReview.Plan(original, edits).Segments, _ => true);

    [Fact]
    public void Edits_are_placed_in_file_order_with_old_line_numbers_and_reasons()
    {
        var plan = InstructionReview.Plan(Rules, [Edit("- write tests", "- write tests first", "second"), Edit("- be concise", "- be extremely concise", "first")]);
        var changes = plan.Segments.Where(s => s.IsChange).ToList();

        Assert.Empty(plan.Unplaced);
        Assert.Equal([2, 4], changes.Select(c => c.OldStart));
        Assert.Equal(["first", "second"], plan.Reasons);
        Assert.Equal("# Rules\r\n- be extremely concise\r\n- use tabs\r\n- write tests first\r\n", LineDiff.Merge(plan.Segments, _ => true));
        Assert.Equal(Rules, LineDiff.Merge(plan.Segments, _ => false));
    }

    [Fact]
    public void Only_accepted_changes_are_merged()
    {
        var plan = InstructionReview.Plan(Rules, [Edit("- be concise", "- be brief"), Edit("- use tabs", "- use spaces")]);
        Assert.Equal("# Rules\r\n- be concise\r\n- use spaces\r\n- write tests\r\n", LineDiff.Merge(plan.Segments, i => i == 1));
    }

    [Fact]
    public void Start_and_end_span_the_lines_between_without_quoting_them()
    {
        Assert.Equal("# Rules\r\n- concise, tabs, tests\r\n", ApplyAll(Rules, Edit("- be concise", "- concise, tabs, tests", end: "- write tests")));
    }

    [Fact]
    public void End_is_the_first_match_after_the_start()
    {
        Assert.Equal("## A\r\nx\r\n---\r\n## B\r\n---\r\n", ApplyAll("## A\r\nold\r\n---\r\n## B\r\n---\r\n", Edit("## A", "## A\nx\n---", end: "---")));
    }

    [Fact]
    public void Empty_replace_deletes_the_lines()
    {
        Assert.Equal("# Rules\r\n- write tests\r\n", ApplyAll(Rules, Edit("- be concise\n", "", end: "- use tabs")));
    }

    [Fact]
    public void Two_line_start_with_lf_endings_matches_a_crlf_file()
    {
        Assert.Equal("# Rules\r\n- be concise, use tabs\r\n- write tests\r\n", ApplyAll(Rules, Edit("- be concise\n- use tabs", "- be concise, use tabs")));
    }

    [Fact]
    public void Indentation_differences_still_match()
    {
        Assert.Equal("# Rules\r\n- be brief\r\n- use tabs\r\n- write tests\r\n", ApplyAll(Rules, Edit("  - be concise  ", "- be brief")));
    }

    [Fact]
    public void A_quoted_part_of_a_line_finds_the_only_line_containing_it()
    {
        Assert.Equal("# Rules\r\n- be brief\r\n- use tabs\r\n- write tests\r\n", ApplyAll(Rules, Edit("be concise", "- be brief")));
        Assert.Empty(InstructionReview.Plan(Rules, [Edit("- ", "x")]).Reasons); // too short to trust
    }

    [Fact]
    public void Missing_repeated_overlapping_and_endless_passages_are_left_out()
    {
        var plan = InstructionReview.Plan("- a\r\n- a\r\n- b\r\n- c\r\n- d\r\n",
        [
            Edit("- zzz", "x", "missing"), Edit("- a", "x", "repeated"), Edit("- b", "x", "kept", end: "- c"),
            Edit("- c", "y", "overlaps"), Edit("- d", "y", "no end", end: "- zzz")
        ]);

        Assert.Equal(["kept"], plan.Reasons);
        Assert.Equal(["missing", "repeated", "overlaps", "no end"], plan.Unplaced.Select(u => u.Why));
    }

    [Fact]
    public void An_empty_file_can_be_filled_and_keeps_its_final_newline()
    {
        Assert.Equal("# Title\r\n- todo\r\n", ApplyAll("\r\n", Edit("", "# Title\n- todo")));
        Assert.Equal("# Title", ApplyAll("", Edit("", "# Title")));
    }

    [Fact]
    public void An_empty_start_on_a_file_with_text_appends_before_its_final_newline()
    {
        Assert.Equal(Rules + "\r\n## More\r\n- x\r\n", ApplyAll(Rules, Edit("", "\n\n## More\n- x")));
        Assert.Equal("- a", ApplyAll("- a", Edit("", "", why: "nothing")).TrimEnd()); // nothing to add: left out
        Assert.Empty(InstructionReview.Plan(Rules, [Edit("", "")]).Reasons);
    }

    [Fact]
    public void Several_appends_keep_their_order_and_are_separate_changes()
    {
        var plan = InstructionReview.Plan(Rules, [Edit("", "## One", "one"), Edit("", "## Two", "two"), Edit("- use tabs", "- use spaces", "mid")]);

        Assert.Equal(["mid", "one", "two"], plan.Reasons);
        Assert.Empty(plan.Unplaced);
        var changes = plan.Segments.Where(s => s.IsChange).ToList();
        Assert.Equal(3, changes.Count);
        Assert.Equal(5, changes[1].OldStart); // appended after line 4, before the trailing empty line
        Assert.Empty(changes[1].OldLines);
        Assert.Equal("# Rules\r\n- be concise\r\n- use tabs\r\n- write tests\r\n## Two\r\n", LineDiff.Merge(plan.Segments, i => i == 2));
    }

    [Fact]
    public void Envelope_structured_output_is_read()
    {
        var json = """{"type":"result","is_error":false,"result":"","structured_output":{"summary":"Tidy.","edits":[{"why":"dup","start":"- a","end":"","replace":"- b"}]}}""";
        var suggestion = InstructionReview.ParseEnvelope(json)!;
        Assert.Equal("Tidy.", suggestion.Summary);
        Assert.Equal(new ReviewEdit("dup", "- a", "", "- b"), Assert.Single(suggestion.Edits));
    }

    [Fact]
    public void Envelope_falls_back_to_json_in_result()
    {
        var json = """{"type":"result","is_error":false,"result":"{\"summary\":\"Fine.\",\"edits\":[]}"}""";
        Assert.Equal("Fine.", InstructionReview.ParseEnvelope(json)!.Summary);
        Assert.Null(InstructionReview.ParseEnvelope("not json"));
    }

    [Fact]
    public void Envelope_surfaces_cli_errors_and_unexpected_replies()
    {
        Assert.Contains("Not logged in", Assert.Throws<InvalidOperationException>(() =>
            InstructionReview.ParseEnvelope("""{"type":"result","is_error":true,"result":"Not logged in"}""")).Message);
        Assert.Throws<InvalidOperationException>(() => InstructionReview.ParseEnvelope("""{"type":"result","is_error":false,"result":"Sorry."}"""));
    }

    [Fact]
    public void Request_says_where_the_file_applies_and_holds_its_text()
    {
        var global = InstructionReview.BuildRequest(File, "- be concise\r\n", repoFolder: null).Replace("\r\n", "\n");
        Assert.Contains("User-wide", global);
        Assert.Contains("<file>\n- be concise\n\n</file>", global);
        Assert.Contains(@"Repository file for C:\repo", InstructionReview.BuildRequest(File, "x", @"C:\repo"));
    }

    [Fact]
    public void Arguments_turn_off_tools_and_the_users_own_settings()
    {
        var args = InstructionReview.Arguments().ToList();
        Assert.Equal("", args[args.IndexOf("--tools") + 1]);
        Assert.Equal("", args[args.IndexOf("--setting-sources") + 1]);
        Assert.Contains("--json-schema", args);
        Assert.Contains("--no-session-persistence", args);
    }

    [LiveFact("claude", optIn: "AGENTHUB_LIVE_REVIEW")]
    public async Task Live_review_of_a_messy_file_returns_placeable_edits()
    {
        const string messy = "# Rules\r\n- Be concise.\r\n- Always be concise in replies.\r\n- Write good code.\r\n- Use tabs.\r\n- Use 4 spaces, never tabs.\r\n";
        var suggestion = await InstructionReview.RunAsync(new ProcessRunner(), "claude", File, messy, null, CancellationToken.None);
        var plan = InstructionReview.Plan(messy, suggestion.Edits);

        output.WriteLine(suggestion.Summary);
        foreach (var e in suggestion.Edits) output.WriteLine($"- {e.Why}\n  start: {e.Start}\n  end: {e.End}\n  replace: {e.Replace}");
        Assert.NotEmpty(plan.Reasons);
        Assert.Empty(plan.Unplaced);
    }
}
