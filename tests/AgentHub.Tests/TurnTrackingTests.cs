using System.IO;
using System.Text.Json;
using AgentHub.Tracking;

namespace AgentHub.Tests;

public class ClaudeTranscriptParserTests
{
    private static string Prompt(string uuid, object content, object? origin = null) => JsonSerializer.Serialize(new
    {
        type = "user",
        uuid,
        sessionId = "s1",
        timestamp = "2026-10-10T01:00:00Z",
        origin = origin ?? new { kind = "human" },
        message = new { role = "user", content }
    });

    private static string ToolResult() => JsonSerializer.Serialize(new
    {
        type = "user",
        uuid = Guid.NewGuid().ToString(),
        toolUseResult = new { },
        message = new { role = "user", content = new object[] { new { type = "tool_result", tool_use_id = "t", content = "ok" } } }
    });

    private static string Assistant(object[] blocks, string? stopReason = null) => JsonSerializer.Serialize(new
    {
        type = "assistant",
        timestamp = "2026-10-10T01:05:00Z",
        message = new { role = "assistant", stop_reason = stopReason, content = blocks }
    });

    private static object Text(string text) => new { type = "text", text };
    private static object Tool(string name, object input) => new { type = "tool_use", id = "t", name, input };

    private static List<TurnEvent> FeedAll(ClaudeTranscriptParser parser, params string[] lines) =>
        lines.SelectMany(parser.Feed).ToList();

    [Fact]
    public void Keeps_prompt_and_closing_text_only()
    {
        var parser = new ClaudeTranscriptParser();
        var events = FeedAll(parser,
            Prompt("u1", "Add a login page"),
            Assistant([Text("Let me look at the routes first."), Tool("Read", new { file_path = "a.cs" })]),
            ToolResult(),
            Assistant([Tool("Edit", new { file_path = @"C:\repo\Login.cs" })]),
            ToolResult(),
            Assistant([Text("Added the login page.")], "end_turn"));

        Assert.Equal(TurnEventKind.Started, events[0].Kind);
        var done = Assert.Single(events, e => e.Kind == TurnEventKind.Completed).Turn;
        Assert.Equal("u1", done.TurnId);
        Assert.Equal("Add a login page", done.UserMessage);
        Assert.Equal("Added the login page.", done.FinalText);
        Assert.Equal(TurnStatus.Complete, done.Status);
        Assert.Equal(2, done.ToolCalls);
        Assert.Equal([@"C:\repo\Login.cs"], done.FilesTouched);
        Assert.Null(parser.Current);
    }

    [Fact]
    public void Joins_text_blocks_after_the_last_tool_call()
    {
        var parser = new ClaudeTranscriptParser();
        var events = FeedAll(parser,
            Prompt("u1", "go"),
            Assistant([Text("Summary.")]),
            Assistant([Text("Next: run tests.")], "end_turn"));

        Assert.Equal("Summary.\n\nNext: run tests.", events.Last().Turn.FinalText);
    }

    [Fact]
    public void Waits_for_the_text_line_when_a_reply_is_split_across_lines()
    {
        var parser = new ClaudeTranscriptParser();
        var events = FeedAll(parser,
            Prompt("u1", "plan it"),
            Assistant([new { type = "thinking", thinking = "..." }], "end_turn"),
            Assistant([Text("Here is the plan.")], "end_turn"));

        var done = Assert.Single(events, e => e.Kind == TurnEventKind.Completed).Turn;
        Assert.Equal("Here is the plan.", done.FinalText);
    }

    [Fact]
    public void Ignores_tool_results_meta_sidechain_and_non_human_prompts()
    {
        var parser = new ClaudeTranscriptParser();
        var meta = JsonSerializer.Serialize(new { type = "user", isMeta = true, uuid = "m", message = new { content = "Caveat" } });
        var side = JsonSerializer.Serialize(new { type = "user", isSidechain = true, uuid = "s", message = new { content = "sub" } });

        var events = FeedAll(parser,
            ToolResult(), meta, side,
            Prompt("bg", "<task-notification>done</task-notification>", new { kind = "task-notification" }),
            "not json", "");

        Assert.Empty(events);
    }

    [Fact]
    public void Strips_system_reminders_and_keeps_text_blocks()
    {
        var parser = new ClaudeTranscriptParser();
        var events = parser.Feed(Prompt("u1", new object[]
        {
            new { type = "text", text = "<system-reminder>ignore me</system-reminder>" },
            new { type = "image" },
            new { type = "text", text = "Fix the header" }
        }));

        Assert.Equal("Fix the header", Assert.Single(events).Turn.UserMessage);
    }

    [Fact]
    public void Records_slash_commands_as_typed_and_skips_command_output()
    {
        var parser = new ClaudeTranscriptParser();
        var started = parser.Feed(Prompt("u1",
            "<command-message>review</command-message>\n<command-name>/review</command-name>\n<command-args>42</command-args>"));
        var output = parser.Feed(Prompt("u2", "<local-command-stdout>Set model</local-command-stdout>"));

        Assert.Equal("/review 42", Assert.Single(started).Turn.UserMessage);
        Assert.Empty(output);
    }

    [Fact]
    public void Next_prompt_interrupts_a_turn_with_output_and_drops_one_without()
    {
        var parser = new ClaudeTranscriptParser();
        FeedAll(parser, Prompt("u1", "/model"));
        var afterSilent = parser.Feed(Prompt("u2", "build it"));
        Assert.Equal([TurnEventKind.Started], afterSilent.Select(e => e.Kind));

        parser.Feed(Assistant([Text("Starting.")]));
        var afterWork = parser.Feed(Prompt("u3", "stop, do this instead"));

        Assert.Equal([TurnEventKind.Completed, TurnEventKind.Started], afterWork.Select(e => e.Kind));
        Assert.Equal("u2", afterWork[0].Turn.TurnId);
        Assert.Equal(TurnStatus.Interrupted, afterWork[0].Turn.Status);
    }

    [Fact]
    public void Reopens_a_finished_turn_when_the_agent_carries_on()
    {
        var parser = new ClaudeTranscriptParser();
        FeedAll(parser, Prompt("u1", "go"), Assistant([Text("Done.")], "end_turn"));

        var more = FeedAll(parser,
            Assistant([Tool("Bash", new { command = "dotnet test" })]),
            Assistant([Text("Tests pass too.")], "end_turn"));

        var again = Assert.Single(more).Turn;
        Assert.Equal("u1", again.TurnId);
        Assert.Equal("Tests pass too.", again.FinalText);
    }

    [Fact]
    public void Settle_completes_a_text_ending_turn_without_a_stop_reason()
    {
        var parser = new ClaudeTranscriptParser();
        FeedAll(parser, Prompt("u1", "go"), Assistant([Tool("Bash", new { command = "x" })]));
        Assert.Empty(parser.Settle()); // still mid-tool

        parser.Feed(Assistant([Text("All done.")]));
        var settled = Assert.Single(parser.Settle());
        Assert.Equal("All done.", settled.Turn.FinalText);
        Assert.Empty(parser.Settle());
    }
}

public class ClaudeSessionLaunchTests
{
    [Theory]
    [InlineData("claude")]
    [InlineData("claude --model opus")]
    [InlineData("claude.exe")]
    [InlineData("\"C:\\Program Files\\Claude\\claude.cmd\" --verbose")]
    public void Assigns_a_session_id_to_fresh_claude_launches(string command)
    {
        Assert.True(ClaudeSessionLaunch.TryAssignSessionId(command, out var decorated, out var id));
        Assert.True(Guid.TryParse(id, out _));
        Assert.Equal($"{command} --session-id {id}", decorated);
    }

    [Theory]
    [InlineData("codex")]
    [InlineData("powershell.exe -NoLogo")]
    [InlineData("claude --resume")]
    [InlineData("claude -c")]
    [InlineData("claude --session-id=abc")]
    [InlineData("claude -p hello")]
    [InlineData("claudette")]
    public void Leaves_other_commands_alone(string command)
    {
        Assert.False(ClaudeSessionLaunch.TryAssignSessionId(command, out var decorated, out _));
        Assert.Equal(command, decorated);
    }

    [Fact]
    public void Encodes_project_folders_like_claude_code()
    {
        Assert.Equal("C--Users-danie-Desktop", ClaudeSessionLaunch.EncodeProjectFolder(@"C:\Users\danie\Desktop"));
        Assert.Equal("C--My-Repos-app-v2", ClaudeSessionLaunch.EncodeProjectFolder(@"C:\My Repos\app.v2"));
    }
}

public sealed class TurnJournalAndWatcherTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "agenthub tests " + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static TranscriptTurn Turn(string id, string final) => new()
    {
        TurnId = id,
        UserMessage = "ask " + id,
        FinalText = final,
        Status = TurnStatus.Complete
    };

    [Fact]
    public void Appends_turns_keeps_the_latest_per_id_and_excludes_the_journal_from_git()
    {
        var repo = Path.Combine(_root, "my repo");
        Directory.CreateDirectory(Path.Combine(repo, ".git", "info"));
        File.WriteAllText(Path.Combine(repo, ".git", "info", "exclude"), "# existing");
        Assert.True(TurnJournal.IsRepositoryRoot(repo));

        TurnJournal.Append(repo, Turn("a", "first"), "claude");
        TurnJournal.Append(repo, Turn("b", "second"), "claude");
        TurnJournal.Append(repo, Turn("a", "first, revised"), "claude");

        var entries = TurnJournal.Read(repo);
        Assert.Equal(["a", "b"], entries.Select(e => e.TurnId));
        Assert.Equal("first, revised", entries[0].FinalText);
        Assert.Equal("claude", entries[0].Agent);

        var exclude = File.ReadAllLines(Path.Combine(repo, ".git", "info", "exclude"));
        Assert.Equal("# existing", exclude[0]);
        Assert.Single(exclude, l => l == "/.agenthub/journal.jsonl");
    }

    [Fact]
    public void Watcher_reads_only_new_complete_lines()
    {
        var workDir = Path.Combine(_root, "work dir");
        var sessionId = Guid.NewGuid().ToString();
        var folder = Path.Combine(_root, "config", "projects", ClaudeSessionLaunch.EncodeProjectFolder(workDir));
        Directory.CreateDirectory(folder);
        var transcript = Path.Combine(folder, sessionId + ".jsonl");

        using var watcher = new TranscriptWatcher(sessionId, workDir, Path.Combine(_root, "config"));
        var events = new List<TurnEvent>();
        watcher.TurnEvent += events.Add;

        watcher.Poll(); // file not written yet
        Assert.Empty(events);

        var prompt = JsonSerializer.Serialize(new { type = "user", uuid = "u1", message = new { content = "hello ✓" } });
        var reply = JsonSerializer.Serialize(new
        {
            type = "assistant",
            message = new { stop_reason = "end_turn", content = new object[] { new { type = "text", text = "hi" } } }
        });

        // The reply arrives in two writes; nothing is parsed until its line is complete.
        File.WriteAllText(transcript, prompt + "\n" + reply[..10]);
        watcher.Poll();
        Assert.Equal([TurnEventKind.Started], events.Select(e => e.Kind));
        Assert.Equal("hello ✓", events[0].Turn.UserMessage);

        File.AppendAllText(transcript, reply[10..] + "\n");
        watcher.Poll();
        watcher.Poll();
        Assert.Equal([TurnEventKind.Started, TurnEventKind.Completed], events.Select(e => e.Kind));
        Assert.Equal("hi", events[1].Turn.FinalText);
    }
}
