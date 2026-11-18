using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentHub.Services;
using AgentHub.Services.Instructions;
using Xunit;
using Xunit.Abstractions;

namespace AgentHub.Tests;

public class InstructionRequestTests(ITestOutputHelper output)
{
    private static readonly InstructionFile File = new(InstructionCli.ClaudeCode, "CLAUDE.md", @"C:\Users\me\.claude\CLAUDE.md", "");

    [Fact]
    public void Request_holds_the_file_then_the_ask()
    {
        var request = InstructionRequest.BuildRequest(File, "# Rules\r\n- be concise\r\n", null, "  Always run tests\r\nbefore commits  ").Replace("\r\n", "\n");

        Assert.Contains("User-wide", request);
        Assert.Contains("<file>\n# Rules\n- be concise\n\n</file>", request);
        Assert.Contains("<request>\nAlways run tests\nbefore commits\n</request>", request);
        Assert.True(request.IndexOf("</file>", StringComparison.Ordinal) < request.IndexOf("<request>", StringComparison.Ordinal));
        Assert.Contains(@"Repository file for C:\repo", InstructionRequest.BuildRequest(File, "x", @"C:\repo", "y"));
    }

    [Fact]
    public void A_very_long_ask_is_cut()
    {
        var request = InstructionRequest.BuildRequest(File, "", null, new string('a', InstructionRequest.MaxAskLength + 500));
        Assert.Contains(new string('a', InstructionRequest.MaxAskLength) + "\n</request>", request.Replace("\r\n", "\n"));
        Assert.DoesNotContain(new string('a', InstructionRequest.MaxAskLength + 1), request);
    }

    [Fact]
    public void An_addition_after_a_line_merges_in_place()
    {
        // The shape the prompt asks for: the anchor line as start, and again as the first line of replace.
        const string rules = "# Rules\r\n- be concise\r\n\r\n# Git\r\n- branch first\r\n";
        var plan = InstructionReview.Plan(rules, [new ReviewEdit("add", "- branch first", "", "- branch first\n- never force-push without asking")]);

        Assert.Empty(plan.Unplaced);
        Assert.Equal("# Rules\r\n- be concise\r\n\r\n# Git\r\n- branch first\r\n- never force-push without asking\r\n",
            LineDiff.Merge(plan.Segments, _ => true));
        // Shown as one added line after line 5, not line 5 removed and re-added.
        var change = Assert.Single(plan.Segments, s => s.IsChange);
        Assert.Empty(change.OldLines);
        Assert.Equal(["- never force-push without asking"], change.NewLines);
        Assert.Equal(6, change.OldStart);
    }

    [Fact]
    public void An_edit_that_changes_nothing_is_dropped_and_unchanged_ends_are_trimmed()
    {
        const string rules = "# A\r\n- one\r\n- two\r\n- three\r\n";
        var plan = InstructionReview.Plan(rules, [
            new ReviewEdit("noop", "- one", "", "- one"),
            new ReviewEdit("middle", "- one", "- three", "- one\n- 2\n- three")]);

        Assert.Empty(plan.Unplaced);
        var change = Assert.Single(plan.Segments, s => s.IsChange);
        Assert.Equal(["- two"], change.OldLines);
        Assert.Equal(["- 2"], change.NewLines);
        Assert.Equal(["middle"], plan.Reasons);
    }

    [Fact]
    public void An_insertion_and_a_change_on_the_next_line_both_apply_in_order()
    {
        const string rules = "# A\r\n- one\r\n- two\r\n";
        var plan = InstructionReview.Plan(rules, [
            new ReviewEdit("change", "- two", "", "- 2"),
            new ReviewEdit("insert", "- one", "", "- one\n- one and a half")]);

        Assert.Empty(plan.Unplaced);
        Assert.Equal("# A\r\n- one\r\n- one and a half\r\n- 2\r\n", LineDiff.Merge(plan.Segments, _ => true));
        Assert.Equal(["insert", "change"], plan.Reasons);
    }

    [LiveFact("claude", optIn: "AGENTHUB_LIVE_REVIEW")]
    public async Task Live_request_adds_a_rule_in_the_right_section()
    {
        const string rules = "# General\r\n- Be concise.\r\n\r\n# Git\r\n- Branch from main for each change.\r\n- Keep PR titles under 70 characters.\r\n";
        var suggestion = await InstructionRequest.RunAsync(new ProcessRunner(), "claude", File, rules, null,
            "never force push unless I say so", CancellationToken.None);
        var plan = InstructionReview.Plan(rules, suggestion.Edits);
        var merged = LineDiff.Merge(plan.Segments, _ => true);

        output.WriteLine(suggestion.Summary);
        output.WriteLine(merged);
        Assert.NotEmpty(plan.Reasons);
        Assert.Empty(plan.Unplaced);
        Assert.Contains("force", merged[merged.IndexOf("# Git", StringComparison.Ordinal)..], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("- Be concise.", merged);
    }
}
