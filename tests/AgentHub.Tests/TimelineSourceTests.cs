using System;
using System.IO;
using System.Linq;
using System.Text;
using AgentHub.Services.Timeline;
using Xunit;

namespace AgentHub.Tests;

public class TimelineSourceTests : IDisposable
{
    // Spaces in the path on purpose: Cockpit folders often have them (OneDrive - Company).
    private readonly string _root = Path.Combine(Path.GetTempPath(), "agenthub timeline " + Guid.NewGuid().ToString("N"));

    public TimelineSourceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string Write(string name, string text)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, text, new UTF8Encoding(false));
        return path;
    }

    // ---- git log ----

    private const string Sep = "\u001f";

    [Fact]
    public void ParseLog_ReadsEveryFieldAndShortensRefNames()
    {
        var log = string.Join("\n",
            $"a1b2c3d4e5f60718293a4b5c6d7e8f9012345678{Sep}0000000000000000000000000000000000000001{Sep}2026-11-03T16:31:29+08:00{Sep}Daznl{Sep}Timeline phase 1{Sep}refs/heads/feature/timeline",
            $"0000000000000000000000000000000000000001{Sep}0000000000000000000000000000000000000002 0000000000000000000000000000000000000003{Sep}2026-11-03T08:00:00Z{Sep}Daniel{Sep}Merge pull request #6{Sep}refs/remotes/origin/main",
            $"0000000000000000000000000000000000000002{Sep}{Sep}2026-10-01T00:00:00Z{Sep}Daniel{Sep}Initial commit{Sep}",
            "garbage line",
            "");

        var commits = TimelineSource.ParseLog(log);

        Assert.Collection(commits,
            c =>
            {
                Assert.Equal("a1b2c3d4e5f60718293a4b5c6d7e8f9012345678", c.Hash);
                Assert.Equal("a1b2c3d", c.Short);
                Assert.Equal(new DateTimeOffset(2026, 11, 3, 16, 31, 29, TimeSpan.FromHours(8)), c.Time);
                Assert.Equal("Daznl", c.Author);
                Assert.Equal("Timeline phase 1", c.Subject);
                Assert.Equal("feature/timeline", c.Branch);
                Assert.False(c.IsMerge);
            },
            c =>
            {
                Assert.True(c.IsMerge);
                Assert.Equal(2, c.Parents.Count);
                Assert.Equal("main", c.Branch);
            },
            c =>
            {
                Assert.Empty(c.Parents);
                Assert.Null(c.Branch);
            });
    }

    [Fact]
    public void ParseLog_HandlesCarriageReturnsAndTags()
    {
        var log = $"1234567{Sep}{Sep}2026-11-03T08:00:00Z{Sep}A{Sep}Subject{Sep}refs/tags/v1.0\r\n";
        var commit = Assert.Single(TimelineSource.ParseLog(log));
        Assert.Equal("v1.0", commit.Branch);
        Assert.Equal("Subject", commit.Subject);
    }

    // ---- last entry time ----

    [Fact]
    public void LastEntryTime_IsTheNewestTimestampNearTheEndOfTheFile()
    {
        var path = Write("s.jsonl",
            "{\"type\":\"user\",\"timestamp\":\"2026-11-03T01:00:00.000Z\"}\n" +
            "{\"type\":\"assistant\",\"timestamp\":\"2026-11-03T01:05:00.000Z\"}\n" +
            "{\"type\":\"ai-title\",\"aiTitle\":\"no time here\"}\n");

        Assert.Equal(new DateTimeOffset(2026, 11, 3, 1, 5, 0, TimeSpan.Zero), TimelineSource.LastEntryTime(path));
    }

    [Fact]
    public void LastEntryTime_IsNullWithoutTimestampsOrFile()
    {
        Assert.Null(TimelineSource.LastEntryTime(Write("empty.jsonl", "")));
        Assert.Null(TimelineSource.LastEntryTime(Path.Combine(_root, "missing.jsonl")));
    }

    // ---- transcript hashes ----

    [Fact]
    public void TranscriptHashes_FindsHexWordsOfSevenToFortyCharactersAndKeepsTheirFirstSeven()
    {
        var path = Write("h.jsonl",
            "commit a1b2c3d done; full 0123456789abcdef0123456789abcdef01234567 and 20261103 " +
            "not abc123 (six) nor abcdefg (g) nor xabc1234 (word before) nor abc1234x (word after) " +
            "nor ABC1234 (upper) nor 0123456789abcdef0123456789abcdef012345678 (41). ok \"deadbeef\"");

        var hashes = TranscriptHashes.Read(path);

        Assert.Equal(["0123456", "2026110", "a1b2c3d", "deadbee"], hashes.OrderBy(h => h));
    }

    [Fact]
    public void TranscriptHashes_ScansAGrowingFileIncrementally()
    {
        var path = Write("live.jsonl", new string('x', 100) + " first 1111111 and tail abc12");
        Assert.Equal(["1111111"], TranscriptHashes.Read(path));

        // The CLI finishes the word it was writing and adds a new one.
        File.AppendAllText(path, "34 then 2222222\n");
        var hashes = TranscriptHashes.Read(path);

        Assert.Equal(["1111111", "2222222", "abc1234"], hashes.OrderBy(h => h));
        Assert.Same(hashes, TranscriptHashes.Read(path)); // unchanged file: cached set returned as is
    }

    [Fact]
    public void TranscriptHashes_ResumingMidWordNeverInventsAHash()
    {
        // The re-read starts 64 bytes before the old end, in the middle of a long hex word; the tail of that word must
        // not be taken for a new hash.
        var prefix = new string('y', 20) + " " + new string('a', 100) + " ";
        var path = Write("mid.jsonl", prefix);
        Assert.Empty(TranscriptHashes.Read(path));

        File.AppendAllText(path, "3333333 ");
        Assert.Equal(["3333333"], TranscriptHashes.Read(path));
    }

    [Fact]
    public void TranscriptHashes_MissingFileGivesNothing()
    {
        Assert.Empty(TranscriptHashes.Read(Path.Combine(_root, "none.jsonl")));
    }
}
