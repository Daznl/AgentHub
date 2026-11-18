using System.IO;
using AgentHub.Hosting;
using Xunit;

namespace AgentHub.Tests;

/// <summary>2026-11-06: run.json, steering.json and events.jsonl writes failed while another process (or a reader) held the file.</summary>
public class SharedFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "AgentHub SharedFile " + Guid.NewGuid().ToString("n")[..8]);

    public SharedFileTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public async Task A_replace_waits_out_a_moment_when_someone_else_has_the_target_open()
    {
        var target = Path.Combine(_dir, "run.json");
        var temp = target + ".tmp";
        await File.WriteAllTextAsync(target, "old");
        await File.WriteAllTextAsync(temp, "new");

        var held = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read); // as a virus scanner opens it
        var release = Task.Delay(150).ContinueWith(_ => held.Dispose());
        await SharedFile.ReplaceAsync(temp, target);
        await release;

        Assert.Equal("new", await File.ReadAllTextAsync(target));
        Assert.False(File.Exists(temp));
    }

    [Fact]
    public void A_replace_that_can_never_succeed_still_fails()
    {
        var target = Path.Combine(_dir, "run.json");
        File.WriteAllText(target, "old");
        Assert.Throws<FileNotFoundException>(() => SharedFile.Replace(target + ".missing", target));
    }

    [Fact]
    public async Task Appends_go_on_while_a_reader_is_part_way_through_the_file()
    {
        var log = Path.Combine(_dir, "events.jsonl");
        await SharedFile.AppendAsync(log, "one\n");
        using (var lines = SharedFile.ReadLinesShared(log).GetEnumerator())
        {
            Assert.True(lines.MoveNext()); // the reader holds the file open here
            await SharedFile.AppendAsync(log, "two\n");
        }
        Assert.Equal(["one", "two"], SharedFile.ReadLinesShared(log).ToArray());
    }
}
