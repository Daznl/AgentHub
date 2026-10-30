using System;
using System.IO;
using System.Linq;
using System.Text;
using AgentHub.Services.Context;
using AgentHub.Services.History;
using Xunit;

namespace AgentHub.Tests;

public class SessionHistoryReaderTests : IDisposable
{
    // Spaces in the path on purpose: Cockpit folders often have them (OneDrive - Company).
    private readonly string _root = Path.Combine(Path.GetTempPath(), "agenthub history " + Guid.NewGuid().ToString("N"));

    public SessionHistoryReaderTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private const string Ts = "\"timestamp\":\"2026-10-29T01:02:03.000Z\"";

    private static readonly string[] ClaudeSession =
    [
        "{\"type\":\"file-history-snapshot\",\"snapshot\":{}}",
        "{\"type\":\"user\",\"isSidechain\":false," + Ts + ",\"origin\":{\"kind\":\"human\"},\"message\":{\"role\":\"user\",\"content\":\"fix the build\"}}",
        "{\"type\":\"user\",\"isMeta\":true," + Ts + ",\"message\":{\"role\":\"user\",\"content\":\"[Image: original 10x10]\"}}",
        "{\"type\":\"assistant\"," + Ts + ",\"message\":{\"content\":[{\"type\":\"thinking\",\"thinking\":\"\",\"signature\":\"abc\"}]}}",
        "{\"type\":\"assistant\"," + Ts + ",\"message\":{\"content\":[{\"type\":\"thinking\",\"thinking\":\"Check the csproj first.\"}]}}",
        "{\"type\":\"assistant\"," + Ts + ",\"message\":{\"content\":[{\"type\":\"tool_use\",\"name\":\"Bash\",\"input\":{\"command\":\"dotnet build\",\"description\":\"Build\"}}]}}",
        "{\"type\":\"user\"," + Ts + ",\"message\":{\"content\":[{\"type\":\"tool_result\",\"is_error\":true,\"content\":[{\"type\":\"text\",\"text\":\"error CS1002\"}]}]}}",
        "{\"type\":\"assistant\"," + Ts + ",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"Fixed the missing semicolon.\"}]}}",
        "{\"type\":\"ai-title\",\"aiTitle\":\"Old title\"}",
        "{\"type\":\"user\"," + Ts + ",\"message\":{\"content\":\"<command-name>/model</command-name>\\n<command-args>opus</command-args>\"}}",
        "{\"type\":\"user\"," + Ts + ",\"message\":{\"content\":\"[Request interrupted by user]\"}}",
        "{\"type\":\"system\",\"subtype\":\"compact_boundary\"," + Ts + "}",
        "{\"type\":\"ai-title\",\"aiTitle\":\"Fix the build\"}"
    ];

    private string WriteClaude(string cwd, string name, string[] lines)
    {
        var dir = Path.Combine(_root, "claude", SessionContextReader.EncodeClaudeProjectName(cwd));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, string.Join("\n", lines) + "\n");
        return path;
    }

    [Fact]
    public void Claude_ReadsEveryKindInOrderAndSkipsBookkeeping()
    {
        var path = WriteClaude(@"C:\Work\My Repo", "s1.jsonl", ClaudeSession);

        var (entries, next) = SessionHistoryReader.ReadEntries(path, ContextSource.ClaudeCode);

        Assert.Equal(new FileInfo(path).Length, next);
        Assert.Collection(entries,
            e => { Assert.Equal(HistoryKind.Prompt, e.Kind); Assert.Equal("fix the build", e.Text); Assert.NotNull(e.Timestamp); },
            e => { Assert.Equal(HistoryKind.Thinking, e.Kind); Assert.Equal("Check the csproj first.", e.Text); },
            e => { Assert.Equal(HistoryKind.ToolCall, e.Kind); Assert.Equal("Bash", e.ToolName); Assert.StartsWith("dotnet build", e.Text); Assert.Contains("description: Build", e.Text); },
            e => { Assert.Equal(HistoryKind.ToolResult, e.Kind); Assert.True(e.IsError); Assert.Equal("error CS1002", e.Text); },
            e => { Assert.Equal(HistoryKind.Reply, e.Kind); Assert.Equal("Fixed the missing semicolon.", e.Text); },
            e => { Assert.Equal(HistoryKind.Prompt, e.Kind); Assert.Equal("/model opus", e.Text); },
            e => { Assert.Equal(HistoryKind.Event, e.Kind); Assert.Equal("Interrupted by user", e.Text); },
            e => { Assert.Equal(HistoryKind.Event, e.Kind); Assert.Equal("Conversation compacted", e.Text); });
    }

    [Fact]
    public void ReadEntries_ContinuesFromOffsetAndLeavesPartialLineForLater()
    {
        var path = WriteClaude(@"C:\Work\My Repo", "live.jsonl", ClaudeSession[..2]);
        var (first, offset) = SessionHistoryReader.ReadEntries(path, ContextSource.ClaudeCode);
        Assert.Single(first);

        // The CLI is midway through writing the next line.
        var reply = ClaudeSession[7];
        File.AppendAllText(path, reply[..20]);
        var (none, sameOffset) = SessionHistoryReader.ReadEntries(path, ContextSource.ClaudeCode, offset);
        Assert.Empty(none);
        Assert.Equal(offset, sameOffset);

        File.AppendAllText(path, reply[20..] + "\n");
        var (more, end) = SessionHistoryReader.ReadEntries(path, ContextSource.ClaudeCode, sameOffset);
        Assert.Equal("Fixed the missing semicolon.", Assert.Single(more).Text);
        Assert.Equal(new FileInfo(path).Length, end);
    }

    [Fact]
    public void ReadEntries_OffsetsCountBytesNotCharacters()
    {
        var unicode = "{\"type\":\"user\"," + Ts + ",\"message\":{\"content\":\"héllo — ✓ 🚀\"}}";
        var path = WriteClaude(@"C:\Work\My Repo", "utf8.jsonl", [unicode]);
        var (_, offset) = SessionHistoryReader.ReadEntries(path, ContextSource.ClaudeCode);
        Assert.Equal(Encoding.UTF8.GetByteCount(unicode) + 1, offset);

        File.AppendAllText(path, ClaudeSession[7] + "\n");
        var (more, _) = SessionHistoryReader.ReadEntries(path, ContextSource.ClaudeCode, offset);
        Assert.Equal(HistoryKind.Reply, Assert.Single(more).Kind);
    }

    [Fact]
    public void LongToolOutputIsTruncated()
    {
        var big = new string('x', SessionHistoryReader.MaxEntryChars + 500);
        var line = "{\"type\":\"user\"," + Ts + ",\"message\":{\"content\":[{\"type\":\"tool_result\",\"content\":\"" + big + "\"}]}}";
        var path = WriteClaude(@"C:\Work\My Repo", "big.jsonl", [line]);

        var entry = Assert.Single(SessionHistoryReader.ReadEntries(path, ContextSource.ClaudeCode).Entries);
        Assert.StartsWith(new string('x', SessionHistoryReader.MaxEntryChars), entry.Text);
        Assert.Contains("500 more characters", entry.Text);
    }

    [Fact]
    public void ListSessions_SummarisesClaudeAndCodexForTheFolderOnly()
    {
        var cwd = @"C:\Work\My Repo";
        WriteClaude(cwd, "s1.jsonl", ClaudeSession);
        WriteClaude(cwd, "empty.jsonl", ["{\"type\":\"file-history-snapshot\"}"]); // opened, never used
        WriteClaude(@"C:\Work\Other", "other.jsonl", ClaudeSession);

        var codexDir = Path.Combine(_root, "codex", "2026", "10", "29");
        Directory.CreateDirectory(codexDir);
        File.WriteAllLines(Path.Combine(codexDir, "rollout-a.jsonl"),
        [
            "{\"type\":\"session_meta\"," + Ts + ",\"payload\":{\"cwd\":\"C:\\\\Work\\\\My Repo\"}}",
            "{\"type\":\"event_msg\"," + Ts + ",\"payload\":{\"type\":\"user_message\",\"message\":\"add tests\"}}"
        ]);
        File.WriteAllLines(Path.Combine(codexDir, "rollout-b.jsonl"),
        [
            "{\"type\":\"session_meta\"," + Ts + ",\"payload\":{\"cwd\":\"C:\\\\Elsewhere\"}}",
            "{\"type\":\"event_msg\"," + Ts + ",\"payload\":{\"type\":\"user_message\",\"message\":\"nope\"}}"
        ]);

        var sessions = SessionHistoryReader.ListSessions(cwd, Path.Combine(_root, "claude"), Path.Combine(_root, "codex"));

        Assert.Equal(2, sessions.Count);
        var claude = Assert.Single(sessions, s => s.Agent == ContextSource.ClaudeCode);
        Assert.Equal("Fix the build", claude.Title); // newest generated title wins
        Assert.Equal("fix the build", claude.FirstPrompt);
        var codex = Assert.Single(sessions, s => s.Agent == ContextSource.Codex);
        Assert.Equal("add tests", codex.FirstPrompt);
        Assert.Null(codex.Title);
    }

    [Fact]
    public void Codex_ReadsPromptsRepliesReasoningAndTools()
    {
        var dir = Path.Combine(_root, "codex-read");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "rollout-x.jsonl");
        File.WriteAllLines(path,
        [
            "{\"type\":\"session_meta\"," + Ts + ",\"payload\":{\"cwd\":\"C:\\\\Work\"}}",
            // The model's user turn carries injected context; the typed prompt comes from the event.
            "{\"type\":\"response_item\"," + Ts + ",\"payload\":{\"type\":\"message\",\"role\":\"user\",\"content\":[{\"type\":\"input_text\",\"text\":\"<environment_context>...\"}]}}",
            "{\"type\":\"event_msg\"," + Ts + ",\"payload\":{\"type\":\"user_message\",\"message\":\"list files\"}}",
            "{\"type\":\"response_item\"," + Ts + ",\"payload\":{\"type\":\"reasoning\",\"summary\":[{\"type\":\"summary_text\",\"text\":\"Use ls.\"}]}}",
            "{\"type\":\"response_item\"," + Ts + ",\"payload\":{\"type\":\"function_call\",\"name\":\"shell\",\"arguments\":\"{\\\"command\\\":[\\\"ls\\\",\\\"-la\\\"]}\"}}",
            "{\"type\":\"response_item\"," + Ts + ",\"payload\":{\"type\":\"function_call_output\",\"output\":\"{\\\"output\\\":\\\"a.txt\\\",\\\"metadata\\\":{}}\"}}",
            "{\"type\":\"event_msg\"," + Ts + ",\"payload\":{\"type\":\"agent_message\",\"message\":\"duplicate of the reply\"}}",
            "{\"type\":\"response_item\"," + Ts + ",\"payload\":{\"type\":\"message\",\"role\":\"assistant\",\"content\":[{\"type\":\"output_text\",\"text\":\"One file: a.txt\"}]}}"
        ]);

        var entries = SessionHistoryReader.ReadEntries(path, ContextSource.Codex).Entries;

        Assert.Equal(
            [HistoryKind.Prompt, HistoryKind.Thinking, HistoryKind.ToolCall, HistoryKind.ToolResult, HistoryKind.Reply],
            entries.Select(e => e.Kind).ToArray());
        Assert.Equal("list files", entries[0].Text);
        Assert.Equal("ls -la", entries[2].Text);
        Assert.Equal("a.txt", entries[3].Text);
        Assert.Equal("One file: a.txt", entries[4].Text);
    }
}
