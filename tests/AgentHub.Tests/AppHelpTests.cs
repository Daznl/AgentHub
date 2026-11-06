using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentHub.Controls;
using AgentHub.Services;
using AgentHub.Services.Help;
using Xunit;
using Xunit.Abstractions;

namespace AgentHub.Tests;

public class AppHelpTests(ITestOutputHelper output)
{
    [Fact]
    public void The_readme_and_docs_are_packed_into_the_build()
    {
        var names = AppHelp.DocNames();
        Assert.Equal("README.md", names[0]);
        Assert.Contains("ARCHITECTURE.md", names);
        Assert.Contains("<doc name=\"README.md\">", AppHelp.Docs);
        Assert.Contains("Cockpit", AppHelp.Docs);
    }

    [Fact]
    public void Request_holds_docs_state_recent_turns_and_the_question_in_order()
    {
        var history = Enumerable.Range(1, 14).Select(i => new HelpTurn(i % 2 == 1, $"turn {i}")).ToList();
        var request = AppHelp.BuildRequest(history, "  How do I add a pane?\r\n ", "Current view: Cockpit", docs: "<doc name=\"README.md\">x</doc>\n");

        var order = new[] { "<agenthub_docs>", "<app_state>", "<conversation>", "<question>" }.Select(t => request.IndexOf(t, StringComparison.Ordinal)).ToList();
        Assert.DoesNotContain(-1, order);
        Assert.Equal(order.OrderBy(i => i), order);
        Assert.DoesNotContain("turn 4\n", request);           // only the newest ten turns
        Assert.Contains("User: turn 5\n", request);
        Assert.Contains("Helper: turn 14\n", request);
        Assert.Contains("<question>\nHow do I add a pane?\n</question>", request);
    }

    [Fact]
    public void Request_without_history_or_state_leaves_those_out_and_cuts_long_questions()
    {
        var request = AppHelp.BuildRequest([], new string('q', AppHelp.MaxQuestionLength + 50), null, docs: "");
        Assert.DoesNotContain("<app_state>", request);
        Assert.DoesNotContain("<conversation>", request);
        Assert.Contains(new string('q', AppHelp.MaxQuestionLength) + "…", request);
    }

    [Fact]
    public void Reply_is_read_with_at_most_three_non_empty_suggestions()
    {
        using var doc = JsonDocument.Parse("""{"answer":" Click **➕ Add Terminal**. ","suggestions":["a"," ","b","c","d"]}""");
        var answer = AppHelp.Read(doc.RootElement);
        Assert.Equal("Click **➕ Add Terminal**.", answer.Answer);
        Assert.Equal(["a", "b", "c"], answer.Suggestions);
    }

    [Fact]
    public void Rich_text_reads_bold_code_bullets_and_headings()
    {
        var spans = RichText.Parse("# Steps\n- Click **📄 Context**, then `Save`.\nDone");
        Assert.Equal(
        [
            new RichSpan("Steps", RichSpanKind.Bold), new RichSpan("\n", RichSpanKind.LineBreak),
            new RichSpan("•  ", RichSpanKind.Plain), new RichSpan("Click ", RichSpanKind.Plain), new RichSpan("📄 Context", RichSpanKind.Bold),
            new RichSpan(", then ", RichSpanKind.Plain), new RichSpan("Save", RichSpanKind.Code), new RichSpan(".", RichSpanKind.Plain),
            new RichSpan("\n", RichSpanKind.LineBreak), new RichSpan("Done", RichSpanKind.Plain)
        ], spans);
        Assert.Equal([new RichSpan("2 * 3 and a lone ** stay", RichSpanKind.Plain)], RichText.Parse("2 * 3 and a lone ** stay"));
    }

    [LiveFact("claude", optIn: "AGENTHUB_LIVE_REVIEW")]
    public async Task Live_helper_answers_about_agenthub_and_declines_other_questions()
    {
        var on = await AppHelp.RunAsync(new ProcessRunner(), "claude", [], "How do I open a second terminal next to my first one?", "Current view: ⚡ Cockpit\nTerminal panes: 1", CancellationToken.None);
        output.WriteLine(on.Answer + "\n> " + string.Join("\n> ", on.Suggestions));
        Assert.Contains("Add Terminal", on.Answer);

        var off = await AppHelp.RunAsync(new ProcessRunner(), "claude", [], "Write me a Python function that reverses a string.", null, CancellationToken.None);
        output.WriteLine(off.Answer);
        Assert.DoesNotContain("def ", off.Answer);
        Assert.Contains("AgentHub", off.Answer);
        Assert.Empty(off.Suggestions);
    }
}
