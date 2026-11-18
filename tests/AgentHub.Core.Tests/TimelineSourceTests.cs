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

    // ---- git log and branch tips ----

    private const string Sep = "\u001f";

    [Fact]
    public void ParseLog_ReadsEveryField()
    {
        var log = string.Join("\n",
            $"a1b2c3d4e5f60718293a4b5c6d7e8f9012345678{Sep}0000000000000000000000000000000000000001{Sep}2026-11-03T16:31:29+08:00{Sep}ada-dev{Sep}Timeline phase 1",
            $"0000000000000000000000000000000000000001{Sep}0000000000000000000000000000000000000002 0000000000000000000000000000000000000003{Sep}2026-11-03T08:00:00Z{Sep}Ada{Sep}Merge pull request #6",
            $"0000000000000000000000000000000000000002{Sep}{Sep}2026-10-01T00:00:00Z{Sep}Ada{Sep}Initial commit\r",
            "garbage line",
            "");

        var commits = TimelineSource.ParseLog(log);

        Assert.Collection(commits,
            c =>
            {
                Assert.Equal("a1b2c3d4e5f60718293a4b5c6d7e8f9012345678", c.Hash);
                Assert.Equal("a1b2c3d", c.Short);
                Assert.Equal(new DateTimeOffset(2026, 11, 3, 16, 31, 29, TimeSpan.FromHours(8)), c.Time);
                Assert.Equal("ada-dev", c.Author);
                Assert.Equal("Timeline phase 1", c.Subject);
                Assert.Null(c.Branch); // the graph fills this in
                Assert.False(c.IsMerge);
            },
            c =>
            {
                Assert.True(c.IsMerge);
                Assert.Equal(2, c.Parents.Count);
            },
            c =>
            {
                Assert.Empty(c.Parents);
                Assert.Equal("Initial commit", c.Subject);
            });
    }

    [Fact]
    public void ParseTips_ReadsLocalAndRemoteHeads_DropsTheSymbolicRemoteHead()
    {
        var refs = string.Join("\n",
            $"1111111111111111111111111111111111111111{Sep}refs/heads/main{Sep}",
            $"2222222222222222222222222222222222222222{Sep}refs/heads/feat/x{Sep}",
            $"3333333333333333333333333333333333333333{Sep}refs/remotes/origin/HEAD{Sep}refs/remotes/origin/main",
            $"3333333333333333333333333333333333333333{Sep}refs/remotes/origin/main{Sep}",
            $"4444444444444444444444444444444444444444{Sep}refs/remotes/upstream/feat/y{Sep}\r",
            "");

        var tips = TimelineSource.ParseTips(refs);

        Assert.Equal(
        [
            new TimelineBranchTip("1111111111111111111111111111111111111111", "main", false),
            new TimelineBranchTip("2222222222222222222222222222222222222222", "feat/x", false),
            new TimelineBranchTip("3333333333333333333333333333333333333333", "main", true),
            new TimelineBranchTip("4444444444444444444444444444444444444444", "feat/y", true)
        ], tips);
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
    public void TranscriptScan_CollectsTheMinutesTheSessionWroteEntries()
    {
        var path = Write("t.jsonl",
            "{\"type\":\"user\",\"timestamp\":\"2026-10-26T06:47:12.345Z\"}\n" +
            "{\"type\":\"assistant\",\"timestamp\":\"2026-10-26T06:47:59.000Z\"}\n" +
            "{\"type\":\"assistant\",\"timestamp\":\"2026-10-26T14:53:00+08:00\"}\n" +
            "{\"type\":\"ai-title\",\"aiTitle\":\"no timestamp here\"}\n");

        var facts = TranscriptScan.Read(path);

        var minute = new DateTimeOffset(2026, 10, 26, 6, 47, 0, TimeSpan.Zero).ToUnixTimeSeconds() / 60;
        var later = new DateTimeOffset(2026, 10, 26, 6, 53, 0, TimeSpan.Zero).ToUnixTimeSeconds() / 60; // the +08:00 offset is honoured
        Assert.Equal([minute, later], facts.ActiveMinutes.OrderBy(m => m));

        File.AppendAllText(path, "{\"type\":\"user\",\"timestamp\":\"2026-10-26T07:00:00.000Z\"}\n");
        Assert.Equal(3, TranscriptScan.Read(path).ActiveMinutes.Count);
    }

    [Fact]
    public void TranscriptScan_MentionsBetween_LooksOnlyAtTheEntriesInTheWindow()
    {
        var path = Write("m.jsonl",
            "{\"type\":\"assistant\",\"timestamp\":\"2026-10-26T06:30:00.000Z\",\"text\":\"git commit -m early subject\"}\n" +
            "{\"type\":\"assistant\",\"timestamp\":\"2026-10-26T06:45:00.000Z\",\"text\":\"git commit -m feat(clusters): location clusters page\"}\n" +
            "{\"type\":\"assistant\",\"timestamp\":\"2026-10-26T07:30:00.000Z\",\"text\":\"later subject\"}\n");
        var commitTime = new DateTimeOffset(2026, 10, 26, 6, 47, 0, TimeSpan.Zero);

        Assert.True(TranscriptScan.MentionsBetween(path, "feat(clusters): location clusters page", commitTime.AddMinutes(-15), commitTime.AddMinutes(2)));
        Assert.False(TranscriptScan.MentionsBetween(path, "early subject", commitTime.AddMinutes(-10), commitTime.AddMinutes(2)));
        Assert.False(TranscriptScan.MentionsBetween(path, "later subject", commitTime.AddMinutes(-15), commitTime.AddMinutes(2)));
        Assert.False(TranscriptScan.MentionsBetween(path, "", commitTime.AddMinutes(-15), commitTime.AddMinutes(2)));
    }

    [Fact]
    public void TranscriptHashes_MissingFileGivesNothing()
    {
        Assert.Empty(TranscriptHashes.Read(Path.Combine(_root, "none.jsonl")));
    }
}
