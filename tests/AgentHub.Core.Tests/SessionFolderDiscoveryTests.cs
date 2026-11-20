using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using AgentHub.Services.Context;
using AgentHub.Services.History;
using Xunit;

namespace AgentHub.Tests;

public class SessionFolderDiscoveryTests : IDisposable
{
    // Spaces in the path on purpose: Cockpit folders often have them (OneDrive - Company).
    private readonly string _root = Path.Combine(Path.GetTempPath(), "agenthub discovery " + Guid.NewGuid().ToString("N"));

    public SessionFolderDiscoveryTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string Claude => Path.Combine(_root, "claude");
    private string Codex => Path.Combine(_root, "codex");
    private string Archive => Path.Combine(_root, "archive");

    private static string Ts(int day) => $"\"timestamp\":\"2026-10-{day:00}T01:02:03.000Z\"";

    private static string[] ClaudeSession(string cwd, string prompt, int day = 1) =>
    [
        "{\"type\":\"file-history-snapshot\",\"snapshot\":{}}",
        "{\"type\":\"user\",\"cwd\":" + JsonSerializer.Serialize(cwd) + "," + Ts(day) + ",\"origin\":{\"kind\":\"human\"},\"message\":{\"role\":\"user\",\"content\":" + JsonSerializer.Serialize(prompt) + "}}",
        "{\"type\":\"assistant\",\"cwd\":" + JsonSerializer.Serialize(cwd) + "," + Ts(day) + ",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"Done.\"}]}}"
    ];

    // Claude Code files a session under the encoded name of the folder it started in, unless told otherwise.
    private string WriteClaude(string root, string cwd, string name, string[] lines, string? projectDir = null, DateTime? written = null)
    {
        var dir = Path.Combine(root, projectDir ?? SessionContextReader.EncodeClaudeProjectName(cwd));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, string.Join("\n", lines) + "\n");
        if (written is { } at) File.SetLastWriteTimeUtc(path, at);
        return path;
    }

    private string WriteCodex(string root, string cwd, string name, string prompt)
    {
        var dir = Path.Combine(root, "2026", "10", "29");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllLines(path,
        [
            "{\"type\":\"session_meta\"," + Ts(29) + ",\"payload\":{\"cwd\":" + JsonSerializer.Serialize(cwd) + "}}",
            "{\"type\":\"event_msg\"," + Ts(29) + ",\"payload\":{\"type\":\"user_message\",\"message\":" + JsonSerializer.Serialize(prompt) + "}}"
        ]);
        return path;
    }

    [Fact]
    public void Finds_every_folder_with_sessions_newest_first_and_counts_what_ListSessions_lists()
    {
        var repo = TestPaths.Native(@"C:\Work\My Repo");
        var other = TestPaths.Native(@"D:\Projects\other");
        WriteClaude(Claude, repo, "a.jsonl", ClaudeSession(repo, "fix the build"), written: new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc));
        WriteClaude(Claude, repo, "b.jsonl", ClaudeSession(repo, "add tests"), written: new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc));
        WriteClaude(Claude, repo, "empty.jsonl", ["{\"type\":\"file-history-snapshot\"}"]); // opened, never used
        WriteClaude(Claude, other, "c.jsonl", ClaudeSession(other, "hello"), written: new DateTime(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc));
        var codex = WriteCodex(Codex, repo, "rollout-a.jsonl", "codex prompt");
        File.SetLastWriteTimeUtc(codex, new DateTime(2026, 10, 3, 0, 0, 0, DateTimeKind.Utc));

        var found = SessionFolderDiscovery.Discover(Claude, Codex);

        Assert.Equal([other, repo], found.Select(f => f.Path));
        var mine = found[1];
        Assert.Equal(2, mine.ClaudeSessions);
        Assert.Equal(1, mine.CodexSessions);
        Assert.Equal(new DateTimeOffset(2026, 10, 3, 0, 0, 0, TimeSpan.Zero), mine.LastActivity);
        Assert.False(mine.Exists);
        Assert.Equal(SessionHistoryReader.ListSessions(repo, Claude, Codex).Count, mine.Sessions);
    }

    [Fact]
    public void Folders_on_disk_are_marked_as_existing()
    {
        var folder = Path.Combine(_root, "real folder");
        Directory.CreateDirectory(folder);
        WriteClaude(Claude, folder, "a.jsonl", ClaudeSession(folder, "hi"));

        var found = Assert.Single(SessionFolderDiscovery.Discover(Claude, Codex));

        Assert.Equal(folder, found.Path);
        Assert.True(found.Exists);
    }

    [Fact]
    public void Archived_sessions_count_once_and_sessions_only_left_in_the_archive_count_too()
    {
        var repo = TestPaths.Native(@"C:\Work\My Repo");
        var lines = ClaudeSession(repo, "kept by the archive");
        WriteClaude(Claude, repo, "both.jsonl", lines);
        WriteClaude(HistoryArchiver.ClaudeArchiveDir(Archive), repo, "both.jsonl", lines);
        WriteClaude(HistoryArchiver.ClaudeArchiveDir(Archive), repo, "gone.jsonl", ClaudeSession(repo, "deleted by Claude Code"));
        WriteClaude(HistoryArchiver.ClaudeArchiveDir(Archive), repo, "both.superseded-20261001T000000Z.jsonl", lines);
        WriteCodex(HistoryArchiver.CodexArchiveDir(Archive), repo, "rollout-old.jsonl", "old codex");

        var found = Assert.Single(SessionFolderDiscovery.Discover(Claude, Codex, Archive));

        Assert.Equal(2, found.ClaudeSessions);
        Assert.Equal(1, found.CodexSessions);
        Assert.Equal(SessionHistoryReader.ListSessions(repo, Claude, Codex, Archive).Count, found.Sessions);
    }

    [Fact]
    public void A_long_folder_shortened_by_Claude_Code_is_still_found()
    {
        var deep = TestPaths.Native(@"C:\Work\" + string.Join(@"\", Enumerable.Repeat("a rather long folder name", 10)));
        var encoded = SessionContextReader.EncodeClaudeProjectName(deep);
        Assert.True(encoded.Length > 200);
        WriteClaude(Claude, deep, "a.jsonl", ClaudeSession(deep, "deep"), projectDir: encoded[..200] + "-1a2b3c");

        var found = Assert.Single(SessionFolderDiscovery.Discover(Claude, Codex));

        Assert.Equal(deep, found.Path);
        Assert.Single(SessionHistoryReader.ListSessions(deep, Claude, Codex));
    }

    [Fact]
    public void A_session_filed_under_another_folder_is_not_credited_where_it_could_not_be_listed()
    {
        // Recorded folder and project folder disagree: listing the recorded folder would not find this session.
        var moved = TestPaths.Native(@"C:\Work\Moved");
        WriteClaude(Claude, moved, "a.jsonl", ClaudeSession(moved, "x"), projectDir: SessionContextReader.EncodeClaudeProjectName(TestPaths.Native(@"C:\Work\Elsewhere")));

        Assert.Empty(SessionFolderDiscovery.Discover(Claude, Codex));
    }

    [Fact]
    public void The_folder_is_read_from_the_end_when_the_first_lines_are_too_long_to_probe()
    {
        var repo = TestPaths.Native(@"C:\Work\Big Paste");
        var cwd = JsonSerializer.Serialize(repo);
        var huge = "{\"type\":\"user\",\"cwd\":" + cwd + "," + Ts(1) + ",\"message\":{\"content\":\"" + new string('x', 300_000) + "\"}}";
        WriteClaude(Claude, repo, "a.jsonl",
        [
            huge,
            "{\"type\":\"assistant\",\"cwd\":" + cwd + "," + Ts(1) + ",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"ok\"}]}}",
            "{\"type\":\"ai-title\",\"aiTitle\":\"Big paste\"}"
        ]);

        var found = Assert.Single(SessionFolderDiscovery.Discover(Claude, Codex));

        Assert.Equal(repo, found.Path);
    }

    [Fact]
    public void Missing_transcript_folders_find_nothing()
    {
        Assert.Empty(SessionFolderDiscovery.Discover(Path.Combine(_root, "none"), Path.Combine(_root, "none either"), Path.Combine(_root, "no archive")));
    }

    [Fact]
    public void Scratch_folders_under_AppData_and_temp_are_temporary()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.True(SessionFolderDiscovery.IsTemporary(Path.Combine(profile, "AppData", "Roaming", "Claude", "scratch-workspaces", "x")));
        Assert.True(SessionFolderDiscovery.IsTemporary(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp", "y")));
        Assert.True(SessionFolderDiscovery.IsTemporary(_root));
        Assert.False(SessionFolderDiscovery.IsTemporary(TestPaths.Native(@"C:\Work\My Repo")));
        Assert.False(SessionFolderDiscovery.IsTemporary(Path.Combine(profile, "AppDataBackup")));
        Assert.False(SessionFolderDiscovery.IsTemporary(Path.Combine(profile, ".claude")));
    }

    [Fact]
    public void AgentHubs_own_working_folders_are_not_offered_as_projects()
    {
        Assert.True(SessionFolderDiscovery.IsAgentHubOwn(Path.Combine(AgentHub.Services.Orchestrator.PaneOrchestrator.DefaultSessionsRoot, "20261116-092607-raptor-project")));
        Assert.True(SessionFolderDiscovery.IsAgentHubOwn(AgentHub.Services.Orchestrator.TemplateHelper.WorkingDirectory));
        Assert.True(SessionFolderDiscovery.IsAgentHubOwn(AgentHub.Services.Instructions.ClaudePrint.WorkingDirectory));
        var data = SessionFolderDiscovery.AgentHubDataRoot;
        Assert.True(SessionFolderDiscovery.IsAgentHubOwn(Path.Combine(data, "test-pane-live", "pane-orchestrators", "20261107-114829-alpha")));
        Assert.True(SessionFolderDiscovery.IsAgentHubOwn(Path.Combine(data, "scratch", "rules check", "session")));
        Assert.False(SessionFolderDiscovery.IsAgentHubOwn(Path.Combine(data, "worktrees", "AgentHub", "some-branch")));
        Assert.False(SessionFolderDiscovery.IsAgentHubOwn(Path.Combine(data + "-other", "project")));
        Assert.False(SessionFolderDiscovery.IsAgentHubOwn(TestPaths.Native(@"C:\Work\My Repo")));
    }
}
