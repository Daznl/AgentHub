using AgentHub.Services.History;
using AgentHub.Services.Orchestrator;
using Xunit;

namespace AgentHub.Tests;

/// <summary>The worker session's conversation as the orchestrator's first message carries it.</summary>
public class ConversationHistoryTests
{
    private static HistoryEntry P(string text) => new(HistoryKind.Prompt, text, null);
    private static HistoryEntry R(string text) => new(HistoryKind.Reply, text, null);
    private static HistoryEntry E(string text) => new(HistoryKind.Event, text, null);

    [Fact]
    public void Every_message_is_kept_with_the_last_text_of_its_reply_and_tool_noise_is_left_out()
    {
        var turns = ConversationHistory.Turns(
        [
            P("build the importer"),
            R("Looking at the files."),
            new(HistoryKind.ToolCall, "Read src/a.cs", null, ToolName: "Read"),
            new(HistoryKind.ToolResult, "file contents", null),
            new(HistoryKind.Thinking, "hmm", null),
            R("Importer built, tests pass."),
            new(HistoryKind.Prompt, "a sub-agent's prompt", null, IsSubAgent: true),
            new(HistoryKind.Reply, "a sub-agent's reply", null, IsSubAgent: true),
            P("now the exporter"),
        ]);

        Assert.Equal(2, turns.Count);
        Assert.Equal(new ConversationHistory.Turn("build the importer", "Importer built, tests pass.", false, false), turns[0]);
        Assert.Equal(new ConversationHistory.Turn("now the exporter", null, false, false), turns[1]);
    }

    [Fact]
    public void Prompts_with_no_reply_between_them_are_one_message_and_interrupts_and_compactions_are_marked()
    {
        var turns = ConversationHistory.Turns(
        [
            P("look at this"), P("[image attached]"), R("I see a chart."),
            P("redo it"), E("Interrupted by user"),
            P("redo it smaller"), R("Done."),
            E("Conversation compacted"),
            P("next"),
        ]);

        Assert.Equal(["look at this\n[image attached]", "redo it", "redo it smaller", "next"], turns.Select(t => t.Message));
        Assert.True(turns[1].Interrupted);
        Assert.True(turns[3].CompactedBefore);
        Assert.False(turns[2].CompactedBefore);
    }

    [Fact]
    public void Format_labels_the_block_and_keeps_only_the_end_of_each_earlier_reply()
    {
        var longReply = "start of a long reply " + new string('x', 5000) + " SUMMARY: all done.";
        var text = ConversationHistory.Format([P("job one"), R(longReply), P("job two"), R("Two done.")], lastReplyFollows: false, lastTurnRunning: false)!;

        Assert.StartsWith(ConversationHistory.Intro, text);
        Assert.EndsWith(ConversationHistory.End, text);
        Assert.Contains("--- Message 1 to the worker ---\njob one", text);
        Assert.Contains("--- Worker's reply to message 1 (last part) ---\n…", text);
        Assert.Contains("SUMMARY: all done.", text);
        Assert.DoesNotContain("start of a long reply", text);
        Assert.Contains("--- Worker's reply to message 2 (last part) ---\nTwo done.", text);
    }

    [Fact]
    public void Format_points_at_the_final_reply_instead_of_repeating_it_and_says_when_the_worker_is_still_working()
    {
        var entries = new List<HistoryEntry> { P("job"), R("Done, tests pass.") };
        var follows = ConversationHistory.Format(entries, lastReplyFollows: true, lastTurnRunning: false)!;
        Assert.Contains("[WORKER FINAL REPLY] below", follows);
        Assert.DoesNotContain("Done, tests pass.", follows);

        var running = ConversationHistory.Format([P("job")], lastReplyFollows: false, lastTurnRunning: true)!;
        Assert.Contains("none yet: the worker is still working on this message", running);

        Assert.Null(ConversationHistory.Format([], lastReplyFollows: false, lastTurnRunning: false));
    }

    [Fact]
    public void A_huge_session_is_shortened_but_keeps_the_first_and_latest_messages()
    {
        var entries = new List<HistoryEntry>();
        for (var i = 1; i <= 400; i++)
        {
            entries.Add(P($"message {i} " + new string('m', 3000)));
            entries.Add(R(new string('r', 3000) + $" reply {i} end"));
        }

        var text = ConversationHistory.Format(entries, lastReplyFollows: false, lastTurnRunning: false)!;

        Assert.True(text.Length <= ConversationHistory.MaxChars + ConversationHistory.Intro.Length + 200, $"{text.Length} chars");
        Assert.Contains("--- Message 1 to the worker ---\nmessage 1 ", text);
        Assert.Contains("--- Message 400 to the worker ---\nmessage 400 ", text);
        Assert.Contains("reply 400 end", text);
        Assert.Contains("more messages and replies left out to fit", text);
    }
}
