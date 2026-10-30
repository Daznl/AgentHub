using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AgentHub.Services.Context;
using Xunit;

namespace AgentHub.Tests;

public class SessionContextReaderTests : IDisposable
{
    // Spaces in the path on purpose: Cockpit folders often have them (OneDrive - Company).
    private readonly string _root = Path.Combine(Path.GetTempPath(), "agenthub context " + Guid.NewGuid().ToString("N"));
    private static readonly IReadOnlySet<string> None = new HashSet<string>();

    public SessionContextReaderTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static string Assistant(long input, long cacheCreate, long cacheRead, bool sidechain = false, string model = "claude-opus-5-5") =>
        "{\"type\":\"assistant\",\"isSidechain\":" + (sidechain ? "true" : "false") +
        ",\"timestamp\":\"2026-10-29T01:02:03.000Z\",\"message\":{\"model\":\"" + model + "\",\"usage\":{\"input_tokens\":" + input +
        ",\"cache_creation_input_tokens\":" + cacheCreate + ",\"cache_read_input_tokens\":" + cacheRead + ",\"output_tokens\":500}}}";

    [Theory]
    [InlineData("claude", ContextSource.ClaudeCode)]
    [InlineData("claude --model opus", ContextSource.ClaudeCode)]
    [InlineData("\"C:\\Program Files\\Claude\\claude.exe\" --resume", ContextSource.ClaudeCode)]
    [InlineData("codex.cmd", ContextSource.Codex)]
    [InlineData("powershell.exe -NoLogo", ContextSource.None)]
    [InlineData("agy", ContextSource.None)]
    public void DetectSource_UsesExecutableName(string commandLine, ContextSource expected) =>
        Assert.Equal(expected, SessionContextReader.DetectSource(commandLine));

    [Fact]
    public void EncodeClaudeProjectName_MatchesClaudeCodeFolderNaming() =>
        Assert.Equal("C--Users-daniel-wallis-barker-OneDrive---IGO-Limited-Desktop-AgentHub-main",
            SessionContextReader.EncodeClaudeProjectName(@"C:\Users\daniel.wallis-barker\OneDrive - IGO Limited\Desktop\AgentHub-main\"));

    [Fact]
    public void Claude_FindsNewestTranscriptAndReadsLastMainThreadTurn()
    {
        var cwd = @"C:\Work\My Repo";
        var projectDir = Path.Combine(_root, SessionContextReader.EncodeClaudeProjectName(cwd));
        Directory.CreateDirectory(projectDir);

        var old = Path.Combine(projectDir, "old.jsonl");
        File.WriteAllText(old, Assistant(1, 1, 1) + "\n");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddHours(-2));

        var current = Path.Combine(projectDir, "current.jsonl");
        File.WriteAllLines(current,
        [
            "{\"type\":\"user\",\"message\":{\"content\":\"hi\"}}",
            Assistant(2, 10_000, 50_000),
            Assistant(9, 9, 900_000, sidechain: true), // sub-agent: its own context, must be ignored
            "{\"type\":\"user\",\"message\":{\"content\":\"tool result\"}}"
        ]);

        var found = SessionContextReader.FindClaudeTranscript(cwd, DateTimeOffset.UtcNow.AddMinutes(-1), None, _root);
        Assert.Equal(current, found);

        var reading = SessionContextReader.ReadClaude(found!, 200_000);
        Assert.NotNull(reading);
        Assert.Equal(60_002, reading!.UsedTokens);
        Assert.Equal(200_000, reading.WindowTokens);
        Assert.True(reading.WindowIsAssumed);
        Assert.Equal("claude-opus-5-5", reading.Model);
        Assert.False(reading.CompactedSinceLastTurn);
    }

    [Fact]
    public void Claude_SkipsTranscriptsOwnedByOtherPanesAndOlderThanSession()
    {
        var cwd = @"C:\Work\Shared";
        var projectDir = Path.Combine(_root, SessionContextReader.EncodeClaudeProjectName(cwd));
        Directory.CreateDirectory(projectDir);
        var mine = Path.Combine(projectDir, "mine.jsonl");
        var theirs = Path.Combine(projectDir, "theirs.jsonl");
        File.WriteAllText(mine, Assistant(1, 0, 0) + "\n");
        File.WriteAllText(theirs, Assistant(1, 0, 0) + "\n");
        File.SetLastWriteTimeUtc(mine, DateTime.UtcNow.AddSeconds(-10));

        Assert.Equal(mine, SessionContextReader.FindClaudeTranscript(cwd, DateTimeOffset.UtcNow.AddMinutes(-1),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { theirs }, _root));
        Assert.Null(SessionContextReader.FindClaudeTranscript(cwd, DateTimeOffset.UtcNow.AddMinutes(10), None, _root));
    }

    [Fact]
    public void Claude_RaisesWindowWhenSessionOutgrowsSetting()
    {
        var path = Path.Combine(_root, "big.jsonl");
        File.WriteAllText(path, Assistant(0, 0, 450_000) + "\n");

        var reading = SessionContextReader.ReadClaude(path, 200_000)!;
        Assert.Equal(1_000_000, reading.WindowTokens);
    }

    [Fact]
    public void Claude_FlagsCompactionAfterLastTurn()
    {
        var path = Path.Combine(_root, "compact.jsonl");
        File.WriteAllLines(path,
        [
            Assistant(0, 0, 500_000),
            "{\"type\":\"system\",\"subtype\":\"compact_boundary\",\"content\":\"Conversation compacted\"}"
        ]);

        Assert.True(SessionContextReader.ReadClaude(path, 1_000_000)!.CompactedSinceLastTurn);
    }

    [Fact]
    public void Claude_FindsUsageBeyondTheTailWindow()
    {
        var path = Path.Combine(_root, "long.jsonl");
        var filler = "{\"type\":\"user\",\"message\":{\"content\":\"" + new string('x', 4000) + "\"}}";
        File.WriteAllLines(path, new[] { Assistant(1, 2, 3) }.Concat(Enumerable.Repeat(filler, 400)));

        Assert.Equal(6, SessionContextReader.ReadClaude(path, 200_000)!.UsedTokens);
    }

    private static string User(string contentJson, string extra = "") =>
        "{\"type\":\"user\",\"isSidechain\":false" + extra +
        ",\"timestamp\":\"2026-10-29T01:02:03.000Z\",\"message\":{\"role\":\"user\",\"content\":" + contentJson + "}}";

    [Fact]
    public void ClaudePrompt_ReturnsLastTypedPromptAndSkipsNoise()
    {
        var path = Path.Combine(_root, "prompt.jsonl");
        File.WriteAllLines(path,
        [
            User("\"first prompt\"", ",\"origin\":{\"kind\":\"human\"}"),
            User("\"fix the build\\nthen run tests\"", ",\"origin\":{\"kind\":\"human\"}"),
            Assistant(1, 0, 0),
            User("[{\"type\":\"tool_result\",\"tool_use_id\":\"t1\",\"content\":\"ok\"}]"),
            User("[{\"type\":\"text\",\"text\":\"Base directory for this skill\"}]", ",\"isMeta\":true"),
            User("\"<task-notification><task-id>b1</task-id></task-notification>\"", ",\"origin\":{\"kind\":\"task-notification\"}"),
            User("\"<command-name>/model</command-name>\""),
            User("\"<bash-input>az login</bash-input>\""),
            User("[{\"type\":\"text\",\"text\":\"[Request interrupted by user]\"}]"),
            User("\"sub-agent task\"", "").Replace("\"isSidechain\":false", "\"isSidechain\":true"),
            Assistant(1, 0, 0)
        ]);

        var prompt = SessionContextReader.ReadClaudePrompt(path)!;
        Assert.Equal("fix the build\nthen run tests", prompt.Text);
        Assert.Equal(new DateTimeOffset(2026, 10, 29, 1, 2, 3, TimeSpan.Zero), prompt.SentAt);
    }

    [Fact]
    public void ClaudePrompt_ReadsTextBlocksAndOlderLinesWithoutOrigin()
    {
        var path = Path.Combine(_root, "blocks.jsonl");
        File.WriteAllLines(path,
        [
            User("\"plain older prompt\""),
            User("[{\"type\":\"image\",\"source\":{}},{\"type\":\"text\",\"text\":\"what is in this screenshot?\"}]", ",\"origin\":{\"kind\":\"human\"}")
        ]);
        Assert.Equal("what is in this screenshot?", SessionContextReader.ReadClaudePrompt(path)!.Text);

        File.WriteAllLines(path, [User("\"plain older prompt\""), Assistant(1, 0, 0)]);
        Assert.Equal("plain older prompt", SessionContextReader.ReadClaudePrompt(path)!.Text);

        File.WriteAllLines(path, [Assistant(1, 0, 0)]);
        Assert.Null(SessionContextReader.ReadClaudePrompt(path));
    }

    [Fact]
    public void CodexPrompt_ReadsLastUserMessageEvent()
    {
        var path = Path.Combine(_root, "rollout-prompt.jsonl");
        File.WriteAllLines(path,
        [
            "{\"type\":\"session_meta\",\"payload\":{\"cwd\":\"C:\\\\Work\"}}",
            "{\"timestamp\":\"2026-10-29T01:00:00.000Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"user_message\",\"message\":\"first\",\"images\":[]}}",
            "{\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"role\":\"user\",\"content\":[{\"type\":\"input_text\",\"text\":\"<environment_context>x</environment_context>\"}]}}",
            "{\"timestamp\":\"2026-10-29T01:05:00.000Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"user_message\",\"message\":\"  add a retry  \",\"images\":[]}}",
            "{\"type\":\"event_msg\",\"payload\":{\"type\":\"agent_message\",\"message\":\"done\"}}"
        ]);

        var prompt = SessionContextReader.ReadCodexPrompt(path)!;
        Assert.Equal("add a retry", prompt.Text);
        Assert.Equal(new DateTimeOffset(2026, 10, 29, 1, 5, 0, TimeSpan.Zero), prompt.SentAt);
    }

    [Fact]
    public void Codex_MatchesRolloutByCwdAndReadsRecordedWindow()
    {
        var cwd = @"C:\Work\Codex Repo";
        var today = DateTime.Now;
        var dayDir = Path.Combine(_root, today.ToString("yyyy"), today.ToString("MM"), today.ToString("dd"));
        Directory.CreateDirectory(dayDir);

        static string Meta(string dir) =>
            "{\"type\":\"session_meta\",\"payload\":{\"cwd\":\"" + dir.Replace("\\", "\\\\") + "\"}}";
        static string Tokens(long total, long window) =>
            "{\"timestamp\":\"2026-10-29T01:02:03.000Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{" +
            "\"total_token_usage\":{\"total_tokens\":999999},\"last_token_usage\":{\"input_tokens\":1,\"total_tokens\":" + total + "}," +
            "\"model_context_window\":" + window + "}}}";

        File.WriteAllLines(Path.Combine(dayDir, "rollout-other.jsonl"), [Meta(@"C:\Elsewhere"), Tokens(1, 272_000)]);
        var mine = Path.Combine(dayDir, "rollout-mine.jsonl");
        File.WriteAllLines(mine, [Meta(cwd), Tokens(40_000, 272_000), Tokens(68_000, 272_000), "{\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":null}}"]);
        File.SetLastWriteTimeUtc(mine, DateTime.UtcNow.AddSeconds(-5));

        var found = SessionContextReader.FindCodexRollout(cwd + "\\", DateTimeOffset.UtcNow.AddMinutes(-1), None, _root);
        Assert.Equal(mine, found);

        var reading = SessionContextReader.ReadCodex(found!)!;
        Assert.Equal(68_000, reading.UsedTokens);
        Assert.Equal(272_000, reading.WindowTokens);
        Assert.False(reading.WindowIsAssumed);
        Assert.Equal(0.25, reading.UsedFraction, 3);
    }
}
