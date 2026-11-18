using System;
using System.IO;
using AgentHub.Services.Context;
using AgentHub.Services.History;
using Xunit;

namespace AgentHub.Tests;

public class SessionResumeTests : IDisposable
{
    // Spaces in the path on purpose: Cockpit folders often have them (OneDrive - Company).
    private readonly string _root = Path.Combine(Path.GetTempPath(), "agenthub resume " + Guid.NewGuid().ToString("N"));

    public SessionResumeTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static SessionSummary Session(string path, ContextSource agent, bool archivedOnly = false) =>
        new(path, agent, "t", "p", DateTimeOffset.Now, DateTimeOffset.Now, 1, archivedOnly);

    [Fact]
    public void BuildCommand_UsesEachCliResumeSyntaxAndKeepsConfiguredFlags()
    {
        var claude = Session(@"C:\x\C--Work\3a7acb60-8157-4ec1-a1c7-8975558e83c9.jsonl", ContextSource.ClaudeCode);
        Assert.Equal("claude --model opus --resume 3a7acb60-8157-4ec1-a1c7-8975558e83c9",
            SessionResume.BuildCommand("claude --model opus", claude));

        var codex = Session(@"C:\x\2026\10\29\rollout-2026-10-29T17-24-21-5973b6c0-94b8-487b-a530-2aeb6098ae0e.jsonl", ContextSource.Codex);
        Assert.Equal("5973b6c0-94b8-487b-a530-2aeb6098ae0e", SessionResume.SessionId(codex));
        Assert.Equal("codex resume 5973b6c0-94b8-487b-a530-2aeb6098ae0e", SessionResume.BuildCommand("codex", codex));
    }

    [Fact]
    public void RestoreFromArchive_CopiesTranscriptAndCompanionFolderBack_WithoutOverwriting()
    {
        var archiveRoot = Path.Combine(_root, "archive");
        var live = Path.Combine(_root, "live projects");
        var archivedDir = Path.Combine(HistoryArchiver.ClaudeArchiveDir(archiveRoot), "C--Work-My-Repo");
        Directory.CreateDirectory(Path.Combine(archivedDir, "abc", "subagents"));
        var archived = Path.Combine(archivedDir, "abc.jsonl");
        File.WriteAllText(archived, "archived\n");
        File.WriteAllText(Path.Combine(archivedDir, "abc", "subagents", "agent-1.jsonl"), "sub\n");

        Assert.True(SessionResume.RestoreFromArchive(Session(archived, ContextSource.ClaudeCode, true), archiveRoot, claudeProjectsDir: live));

        var restored = Path.Combine(live, "C--Work-My-Repo", "abc.jsonl");
        Assert.Equal("archived\n", File.ReadAllText(restored));
        Assert.Equal("sub\n", File.ReadAllText(Path.Combine(live, "C--Work-My-Repo", "abc", "subagents", "agent-1.jsonl")));

        File.WriteAllText(restored, "live and newer\n");
        Assert.True(SessionResume.RestoreFromArchive(Session(archived, ContextSource.ClaudeCode, true), archiveRoot, claudeProjectsDir: live));
        Assert.Equal("live and newer\n", File.ReadAllText(restored));
    }

    [Fact]
    public void FindResumedTranscript_FollowsTheExactSessionBeingContinued()
    {
        var cwd = @"C:\Work\My Repo";
        var projects = Path.Combine(_root, "projects");
        var dir = Path.Combine(projects, SessionContextReader.EncodeClaudeProjectName(cwd));
        Directory.CreateDirectory(dir);
        const string id = "0d617d10-f77d-4fd5-ae54-13d634f1e6f7";
        var path = Path.Combine(dir, id + ".jsonl");
        File.WriteAllText(path, "{}\n");

        Assert.Equal(path, SessionContextReader.FindResumedTranscript($"claude --resume {id}", cwd, claudeProjectsDir: projects));
        Assert.Equal(path, SessionContextReader.FindResumedTranscript($"claude --model opus -r {id}", cwd, claudeProjectsDir: projects));
        Assert.Null(SessionContextReader.FindResumedTranscript("claude", cwd, claudeProjectsDir: projects));
        Assert.Null(SessionContextReader.FindResumedTranscript("claude --resume", cwd, claudeProjectsDir: projects));
        Assert.Null(SessionContextReader.FindResumedTranscript($"claude --resume {Guid.NewGuid()}", cwd, claudeProjectsDir: projects));

        var codexDir = Path.Combine(_root, "codex", "2026", "10", "29");
        Directory.CreateDirectory(codexDir);
        var rollout = Path.Combine(codexDir, $"rollout-2026-10-29T17-24-21-{id}.jsonl");
        File.WriteAllText(rollout, "{}\n");
        Assert.Equal(rollout, SessionContextReader.FindResumedTranscript($"codex resume {id}", cwd, codexSessionsDir: Path.Combine(_root, "codex")));
    }

    [Fact]
    public void FindClaudeTranscript_CreatedSince_IgnoresAnOlderSessionStillBeingWritten()
    {
        var cwd = @"C:\Work\My Repo";
        var projects = Path.Combine(_root, "projects");
        var dir = Path.Combine(projects, SessionContextReader.EncodeClaudeProjectName(cwd));
        Directory.CreateDirectory(dir);

        // Another window's session: created an hour ago, written just now.
        var other = Path.Combine(dir, "other.jsonl");
        File.WriteAllText(other, "{}\n");
        File.SetCreationTimeUtc(other, DateTime.UtcNow.AddHours(-1));
        var launched = DateTimeOffset.UtcNow.AddMinutes(-1);
        var none = new System.Collections.Generic.HashSet<string>();

        Assert.Equal(other, SessionContextReader.FindClaudeTranscript(cwd, launched, none, projects));
        Assert.Null(SessionContextReader.FindClaudeTranscript(cwd, launched, none, projects, createdSince: true));

        var mine = Path.Combine(dir, "mine.jsonl");
        File.WriteAllText(mine, "{}\n");
        File.SetLastWriteTimeUtc(mine, DateTime.UtcNow.AddSeconds(-30)); // older write than the other session
        Assert.Equal(mine, SessionContextReader.FindClaudeTranscript(cwd, launched, none, projects, createdSince: true));
    }

    [Fact]
    public void RestoreFromArchive_LeavesLiveSessionsAlone()
    {
        var livePath = Path.Combine(_root, "live", "s.jsonl");
        Assert.True(SessionResume.RestoreFromArchive(Session(livePath, ContextSource.ClaudeCode), Path.Combine(_root, "archive")));
        Assert.False(Directory.Exists(Path.Combine(_root, "archive")));
    }
}
