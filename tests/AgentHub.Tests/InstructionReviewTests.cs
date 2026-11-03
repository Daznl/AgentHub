using System;
using System.Linq;
using AgentHub.Services.Instructions;
using Xunit;

namespace AgentHub.Tests;

public class LineDiffTests
{
    private const string Old = "# Rules\r\n- be concise\r\n- use tabs\r\n- write tests\r\n";

    [Fact]
    public void Same_text_has_no_changes()
    {
        Assert.DoesNotContain(LineDiff.Compute(Old, Old), s => s.IsChange);
    }

    [Fact]
    public void Separate_edits_become_separate_changes_with_old_line_numbers()
    {
        var revised = "# Rules\r\n- be extremely concise\r\n- use tabs\r\n- write tests first\r\n- run lint\r\n";
        var changes = LineDiff.Compute(Old, revised).Where(s => s.IsChange).ToList();

        Assert.Equal(2, changes.Count);
        Assert.Equal(2, changes[0].OldStart);
        Assert.Equal(["- be concise"], changes[0].OldLines);
        Assert.Equal(["- be extremely concise"], changes[0].NewLines);
        Assert.Equal(4, changes[1].OldStart);
        Assert.Equal(["- write tests"], changes[1].OldLines);
        Assert.Equal(["- write tests first", "- run lint"], changes[1].NewLines);
    }

    [Fact]
    public void Merge_all_gives_revised_and_none_gives_original()
    {
        var revised = "# Rules\r\n- be extremely concise\r\n- write tests\r\n- run lint\r\n";
        var segments = LineDiff.Compute(Old, revised);

        Assert.Equal(revised, LineDiff.Merge(segments, _ => true));
        Assert.Equal(Old, LineDiff.Merge(segments, _ => false));
    }

    [Fact]
    public void Merge_keeps_only_accepted_changes()
    {
        var revised = "# Rules\r\n- be extremely concise\r\n- use tabs\r\n- write tests first\r\n";
        var merged = LineDiff.Merge(LineDiff.Compute(Old, revised), i => i == 1);
        Assert.Equal("# Rules\r\n- be concise\r\n- use tabs\r\n- write tests first\r\n", merged);
    }

    [Fact]
    public void Line_endings_alone_are_not_changes()
    {
        Assert.DoesNotContain(LineDiff.Compute(Old, Old.Replace("\r\n", "\n")), s => s.IsChange);
    }

    [Fact]
    public void Empty_original_is_one_addition()
    {
        var changes = LineDiff.Compute("", "# Title\r\n").Where(s => s.IsChange).ToList();
        Assert.Single(changes);
        Assert.Equal("# Title\r\n", LineDiff.Merge(LineDiff.Compute("", "# Title\r\n"), _ => true));
    }
}

public class InstructionReviewTests
{
    private static readonly InstructionFile File = new(InstructionCli.ClaudeCode, "CLAUDE.md", @"C:\Users\me\.claude\CLAUDE.md", "");

    [Fact]
    public void Parse_reads_changes_and_revised_file()
    {
        var reply = "Sure.\n<changes>\n- Merged two duplicate rules\n- Made the test rule specific\n</changes>\n<revised>\n# Rules\n- be concise\n</revised>";
        var suggestion = InstructionReview.Parse(reply);

        Assert.Equal(["Merged two duplicate rules", "Made the test rule specific"], suggestion.Changes);
        Assert.Equal("# Rules\r\n- be concise", suggestion.RevisedText);
    }

    [Fact]
    public void Parse_strips_a_code_fence_round_the_whole_file()
    {
        var suggestion = InstructionReview.Parse("<changes>\n- x\n</changes>\n<revised>\n```markdown\n# Rules\n```\n</revised>");
        Assert.Equal("# Rules", suggestion.RevisedText);
    }

    [Fact]
    public void Parse_keeps_a_reply_cut_off_before_the_closing_tag()
    {
        Assert.Equal("# Rules\r\n- a", InstructionReview.Parse("<revised>\n# Rules\n- a").RevisedText);
    }

    [Fact]
    public void Parse_without_a_revised_file_fails()
    {
        Assert.Throws<InvalidOperationException>(() => InstructionReview.Parse("I could not review this."));
    }

    [Theory]
    [InlineData("a\r\nb\r\n", "a\r\nb", "a\r\nb\r\n")]
    [InlineData("a\r\nb", "a\r\nb\r\n\r\n", "a\r\nb")]
    public void Revised_text_takes_the_original_file_ending(string original, string revised, string expected)
    {
        var aligned = InstructionReview.AlignTrailingNewline(original, new ReviewSuggestion([], revised));
        Assert.Equal(expected, aligned.RevisedText);
    }

    [Fact]
    public void ExtractResult_reads_the_json_envelope()
    {
        Assert.Equal("<revised>x</revised>", InstructionReview.ExtractResult("{\"type\":\"result\",\"is_error\":false,\"result\":\"<revised>x</revised>\"}\r\n"));
        Assert.Null(InstructionReview.ExtractResult("not json"));
    }

    [Fact]
    public void ExtractResult_surfaces_cli_errors()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            InstructionReview.ExtractResult("{\"type\":\"result\",\"is_error\":true,\"result\":\"Not logged in\"}"));
        Assert.Contains("Not logged in", ex.Message);
    }

    [Fact]
    public void Request_says_where_the_file_applies_and_holds_its_text()
    {
        var global = InstructionReview.BuildRequest(File, "- be concise", repoFolder: null);
        Assert.Contains("User-wide", global);
        Assert.Contains("<file>\n- be concise\n</file>", global.Replace("\r\n", "\n"));

        Assert.Contains(@"Repository file for C:\repo", InstructionReview.BuildRequest(File, "x", @"C:\repo"));
    }

    [LiveFact("claude", optIn: "AGENTHUB_LIVE_REVIEW")]
    public async System.Threading.Tasks.Task Live_review_of_a_messy_file_returns_applicable_changes()
    {
        const string messy = "# Rules\r\n- Be concise.\r\n- Always be concise in replies.\r\n- Write good code.\r\n- Use tabs.\r\n- Use 4 spaces, never tabs.\r\n";
        var suggestion = await InstructionReview.RunAsync(new AgentHub.Services.ProcessRunner(), "claude", File, messy, null,
            System.Threading.CancellationToken.None);

        Assert.NotEmpty(suggestion.Changes);
        Assert.NotEqual(messy, suggestion.RevisedText);
        Assert.EndsWith("\r\n", suggestion.RevisedText);
        Assert.Contains(LineDiff.Compute(messy, suggestion.RevisedText), s => s.IsChange);
        Console.WriteLine(string.Join("\n", suggestion.Changes) + "\n---\n" + suggestion.RevisedText);
    }

    [Fact]
    public void Arguments_turn_every_tool_off()
    {
        var args = InstructionReview.Arguments().ToList();
        Assert.Equal("", args[args.IndexOf("--tools") + 1]);
        Assert.Contains("-p", args);
        Assert.Contains("--no-session-persistence", args);
    }
}
