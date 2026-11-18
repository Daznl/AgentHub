using System;
using System.IO;
using System.Linq;
using AgentHub.Services.Context;
using AgentHub.Services.History;
using Xunit;

namespace AgentHub.Tests;

public class HistoryArchiverTests : IDisposable
{
    // Spaces in the path on purpose: Cockpit folders often have them (OneDrive - Company).
    private readonly string _root = Path.Combine(Path.GetTempPath(), "agenthub archive " + Guid.NewGuid().ToString("N"));
    private string Source => Path.Combine(_root, "source projects");
    private string Archive => Path.Combine(_root, "archive", "claude");

    public HistoryArchiverTests() => Directory.CreateDirectory(Source);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private const string Prompt = "{\"type\":\"user\",\"timestamp\":\"2026-10-29T01:02:03.000Z\",\"message\":{\"content\":\"keep me\"}}";
    private const string Reply = "{\"type\":\"assistant\",\"timestamp\":\"2026-10-29T01:02:04.000Z\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"kept\"}]}}";

    private string WriteSource(string relative, string content)
    {
        var path = Path.Combine(Source, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Sync_CopiesNewFilesIncludingSubfolders_ThenSkipsUnchanged()
    {
        WriteSource(@"C--Work-Repo\s1.jsonl", Prompt + "\n");
        WriteSource(@"C--Work-Repo\s1\subagents\agent-a.jsonl", Reply + "\n");

        var first = HistoryArchiver.Sync(Source, Archive);
        Assert.Equal(2, first.Copied);
        Assert.Equal(Prompt + "\n", File.ReadAllText(Path.Combine(Archive, @"C--Work-Repo\s1.jsonl")));
        Assert.True(File.Exists(Path.Combine(Archive, @"C--Work-Repo\s1\subagents\agent-a.jsonl")));

        var second = HistoryArchiver.Sync(Source, Archive);
        Assert.Equal(0, second.Copied + second.Appended + second.Superseded);
        Assert.Equal(0, second.BytesWritten);
    }

    [Fact]
    public void Sync_AppendsOnlyNewBytesOfAGrowingTranscript()
    {
        var src = WriteSource(@"C--Work-Repo\live.jsonl", Prompt + "\n");
        HistoryArchiver.Sync(Source, Archive);

        File.AppendAllText(src, Reply + "\n");
        var result = HistoryArchiver.Sync(Source, Archive);

        Assert.Equal(1, result.Appended);
        Assert.Equal(Reply.Length + 1, result.BytesWritten);
        Assert.Equal(File.ReadAllText(src), File.ReadAllText(Path.Combine(Archive, @"C--Work-Repo\live.jsonl")));
    }

    [Fact]
    public void Sync_NeverDeletes_AndKeepsTheOldCopyWhenATranscriptIsRewritten()
    {
        var src = WriteSource(@"C--Work-Repo\s.jsonl", Prompt + "\n" + Reply + "\n");
        var gone = WriteSource(@"C--Work-Repo\old.jsonl", Prompt + "\n");
        HistoryArchiver.Sync(Source, Archive);

        File.Delete(gone);                       // CLI cleanup
        File.WriteAllText(src, Reply + "\n");    // rewritten shorter
        var result = HistoryArchiver.Sync(Source, Archive);

        Assert.Equal(1, result.Superseded);
        var dir = Path.Combine(Archive, "C--Work-Repo");
        Assert.True(File.Exists(Path.Combine(dir, "old.jsonl")));
        Assert.Equal(Reply + "\n", File.ReadAllText(Path.Combine(dir, "s.jsonl")));
        var kept = Assert.Single(Directory.GetFiles(dir, "s.superseded-*.jsonl"));
        Assert.Equal(Prompt + "\n" + Reply + "\n", File.ReadAllText(kept));
    }

    [Fact]
    public void ListSessions_IncludesSessionsOnlyInTheArchive_AndPrefersTheLongerCopy()
    {
        var cwd = @"C:\Work\My Repo";
        var project = SessionContextReader.EncodeClaudeProjectName(cwd);
        var both = WriteSource(Path.Combine(project, "both.jsonl"), Prompt + "\n");
        var deleted = WriteSource(Path.Combine(project, "deleted.jsonl"), Prompt + "\n");
        HistoryArchiver.Sync(Source, Archive);
        File.Delete(deleted);
        File.AppendAllText(both, Reply + "\n");

        var archiveRoot = Path.Combine(_root, "archive");
        var sessions = SessionHistoryReader.ListSessions(cwd, Source, Path.Combine(_root, "no codex"), archiveRoot);

        Assert.Equal(2, sessions.Count);
        var archivedOnly = Assert.Single(sessions, s => s.ArchivedOnly);
        Assert.StartsWith(HistoryArchiver.ClaudeArchiveDir(archiveRoot), archivedOnly.FilePath);
        Assert.Equal("deleted.jsonl", Path.GetFileName(archivedOnly.FilePath));
        var live = Assert.Single(sessions, s => !s.ArchivedOnly);
        Assert.Equal(both, live.FilePath);
    }
}
