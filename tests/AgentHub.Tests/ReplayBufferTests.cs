using System.Text;
using AgentHub.Hosting;

namespace AgentHub.Tests;

public class ReplayBufferTests
{
    [Fact]
    public void Keeps_everything_while_it_fits()
    {
        var buffer = new ReplayBuffer(16);
        buffer.Append("hello "u8);
        buffer.Append("world"u8);

        Assert.Equal("hello world", Encoding.UTF8.GetString(buffer.Snapshot()));
        Assert.Equal(11, buffer.Count);
        Assert.Equal(11, buffer.TotalAppended);
    }

    [Fact]
    public void Drops_the_oldest_bytes_once_full()
    {
        var buffer = new ReplayBuffer(8);
        buffer.Append("abcdef"u8);
        buffer.Append("ghij"u8);     // wraps: "cdefghij"
        Assert.Equal("cdefghij", Encoding.UTF8.GetString(buffer.Snapshot()));

        buffer.Append("k"u8);
        Assert.Equal("defghijk", Encoding.UTF8.GetString(buffer.Snapshot()));
        Assert.Equal(11, buffer.TotalAppended);
    }

    [Fact]
    public void A_write_larger_than_the_ring_keeps_its_tail()
    {
        var buffer = new ReplayBuffer(4);
        buffer.Append("0123456789"u8);
        Assert.Equal("6789", Encoding.UTF8.GetString(buffer.Snapshot()));

        buffer.Append("ab"u8);
        Assert.Equal("89ab", Encoding.UTF8.GetString(buffer.Snapshot()));
    }

    [Fact]
    public void Snapshot_never_starts_inside_a_multibyte_character()
    {
        var buffer = new ReplayBuffer(5);
        buffer.Append(Encoding.UTF8.GetBytes("a€b"));   // a, E2 82 AC, b = 5 bytes, exactly full
        buffer.Append("c"u8);                            // drops 'a': ring starts at E2 (a valid start)
        Assert.Equal("€bc", Encoding.UTF8.GetString(buffer.Snapshot()));

        buffer.Append("d"u8);                            // drops E2: ring starts at 82, a continuation byte
        Assert.Equal("bcd", Encoding.UTF8.GetString(buffer.Snapshot()));
    }

    [Fact]
    public void Slice_from_an_offset_is_the_tail_when_held_and_the_whole_ring_when_not()
    {
        var buffer = new ReplayBuffer(8);
        buffer.Append("abcdef"u8);
        Assert.Equal(0, buffer.Start);
        Assert.Equal(6, buffer.TotalAppended);

        var tail = buffer.SnapshotFrom(4);
        Assert.Equal((4L, "ef"), (tail.Offset, Encoding.ASCII.GetString(tail.Bytes)));
        var end = buffer.SnapshotFrom(6);
        Assert.Equal((6L, ""), (end.Offset, Encoding.ASCII.GetString(end.Bytes)));
        var none = buffer.SnapshotFrom(-1);
        Assert.Equal((0L, "abcdef"), (none.Offset, Encoding.ASCII.GetString(none.Bytes)));

        buffer.Append("ghij"u8);   // 10 written, 8 kept: the ring now starts at offset 2
        Assert.Equal(2, buffer.Start);
        var kept = buffer.SnapshotFrom(7);
        Assert.Equal((7L, "hij"), (kept.Offset, Encoding.ASCII.GetString(kept.Bytes)));
        var gone = buffer.SnapshotFrom(1);
        Assert.Equal((2L, "cdefghij"), (gone.Offset, Encoding.ASCII.GetString(gone.Bytes)));
        var ahead = buffer.SnapshotFrom(11);
        Assert.Equal((2L, "cdefghij"), (ahead.Offset, Encoding.ASCII.GetString(ahead.Bytes)));
    }

    [Fact]
    public void A_slice_the_client_asked_for_keeps_its_offset_even_inside_a_character()
    {
        var buffer = new ReplayBuffer(16);
        buffer.Append(Encoding.UTF8.GetBytes("a€b"));   // a, E2 82 AC, b
        var asked = buffer.SnapshotFrom(2);              // starts at 82, a continuation byte: the client drops it
        Assert.Equal(2, asked.Offset);
        Assert.Equal(new byte[] { 0x82, 0xAC, (byte)'b' }, asked.Bytes);

        var wrapped = new ReplayBuffer(5);
        wrapped.Append(Encoding.UTF8.GetBytes("a€b"));
        wrapped.Append("cd"u8);                          // ring holds 82 AC 'b' 'c' 'd' from offset 2; trimmed to 'b' at 4
        var whole = wrapped.SnapshotFrom(-1);
        Assert.Equal((4L, "bcd"), (whole.Offset, Encoding.UTF8.GetString(whole.Bytes)));
    }

    [Fact]
    public void Many_small_appends_match_one_big_tail()
    {
        var buffer = new ReplayBuffer(1000);
        var all = new StringBuilder();
        var random = new Random(7);
        for (var i = 0; i < 500; i++)
        {
            var chunk = new string((char)('a' + random.Next(26)), random.Next(1, 40));
            all.Append(chunk);
            buffer.Append(Encoding.ASCII.GetBytes(chunk));
        }
        var expected = all.ToString();
        expected = expected[^Math.Min(1000, expected.Length)..];
        Assert.Equal(expected, Encoding.ASCII.GetString(buffer.Snapshot()));
    }
}
