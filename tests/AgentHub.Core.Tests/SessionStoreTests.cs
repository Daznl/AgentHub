using System.IO;
using AgentHub.Hosting;

namespace AgentHub.Tests;

public sealed class SessionStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "agenthub sessions " + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Record_round_trips_with_a_path_that_has_spaces()
    {
        var store = new SessionStore(_root);
        var record = new SessionRecord
        {
            Id = SessionStore.NewId(),
            CommandLine = "claude --resume abc",
            WorkingDirectory = @"C:\Projects\My Repo\sub folder",
            Title = "Terminal 2 · Claude Code",
            SessionName = "My Repo",
            IsAgent = true,
            Cols = 150,
            Rows = 40,
            StartedAt = new DateTimeOffset(2026, 11, 4, 14, 5, 0, TimeSpan.FromHours(8))
        };
        store.Save(record);

        var back = store.Load(record.Id);
        Assert.NotNull(back);
        Assert.Equal(record.CommandLine, back!.CommandLine);
        Assert.Equal(record.WorkingDirectory, back.WorkingDirectory);
        Assert.Equal(record.Title, back.Title);
        Assert.Equal(record.SessionName, back.SessionName);
        Assert.True(back.IsAgent);
        Assert.Equal((150, 40), (back.Cols, back.Rows));
        Assert.Equal(record.StartedAt, back.StartedAt);
        Assert.Null(back.ExitCode);
        Assert.False(back.HasExited);
        Assert.False(File.Exists(store.RecordPath(record.Id) + ".tmp"));
    }

    [Fact]
    public void Lists_newest_first_and_skips_broken_records()
    {
        var store = new SessionStore(_root);
        store.Save(new SessionRecord { Id = "older", StartedAt = DateTimeOffset.Now.AddHours(-2) });
        store.Save(new SessionRecord { Id = "newer", StartedAt = DateTimeOffset.Now.AddHours(-1) });
        Directory.CreateDirectory(store.Directory("broken"));
        File.WriteAllText(store.RecordPath("broken"), "{ not json");
        Directory.CreateDirectory(store.Directory("empty"));

        var all = store.ListAll();
        Assert.Equal(new[] { "newer", "older" }, all.Select(r => r.Id));
    }

    [Fact]
    public void Screen_snapshot_round_trips_and_a_record_without_replay_start_reads_unknown()
    {
        var store = new SessionStore(_root);
        var screen = new ScreenSnapshot
        {
            Offset = 1_234_567,
            Cols = 145,
            Rows = 47,
            Screen = "\u001b[1;1H\u001b[38;2;255;107;90m╭─ box ─╮\r\n│ ✓ ok │\u001b[0m\u001b[47;3H",
            SavedAt = new DateTimeOffset(2026, 11, 4, 16, 30, 0, TimeSpan.FromHours(8))
        };
        store.SaveScreen("with screen", screen);
        var back = store.LoadScreen("with screen");
        Assert.NotNull(back);
        Assert.Equal((screen.Offset, screen.Cols, screen.Rows, screen.Screen, screen.SavedAt), (back!.Offset, back.Cols, back.Rows, back.Screen, back.SavedAt));
        Assert.False(File.Exists(store.ScreenPath("with screen") + ".tmp"));
        Assert.Null(store.LoadScreen("never saved"));

        Directory.CreateDirectory(store.Directory("old"));
        File.WriteAllText(store.RecordPath("old"), "{ \"Id\": \"old\", \"ExitCode\": 0 }");
        Assert.Equal(-1, store.Load("old")!.ReplayStart);
    }

    [Fact]
    public void Missing_root_lists_nothing_and_unknown_id_loads_null()
    {
        var store = new SessionStore(Path.Combine(_root, "never made"));
        Assert.Empty(store.ListAll());
        Assert.Null(store.Load("nothing"));
    }

    [Fact]
    public void A_record_without_a_host_pid_is_not_alive_nor_is_a_dead_pid()
    {
        Assert.False(SessionStore.IsHostAlive(new SessionRecord { Id = "x" }));
        // This test's own process is alive but is not a session host.
        Assert.False(SessionStore.IsHostAlive(new SessionRecord { Id = "x", HostPid = Environment.ProcessId }));
        Assert.False(SessionStore.IsHostAlive(new SessionRecord { Id = "x", HostPid = int.MaxValue - 1 }));
    }

    [Fact]
    public void Delete_removes_the_session_folder()
    {
        var store = new SessionStore(_root);
        store.Save(new SessionRecord { Id = "gone" });
        File.WriteAllBytes(store.ReplayPath("gone"), [1, 2, 3]);
        store.Delete("gone");
        Assert.False(Directory.Exists(store.Directory("gone")));
        store.Delete("gone"); // idempotent
    }

    [Fact]
    public void Ids_sort_by_time_and_are_unique()
    {
        var a = SessionStore.NewId();
        var b = SessionStore.NewId();
        Assert.NotEqual(a, b);
        Assert.Matches(@"^\d{8}-\d{6}-[0-9a-f]{8}$", a);
        Assert.Equal("agenthub-session-" + a, SessionStore.PipeName(a));
    }

    [Fact]
    public void A_process_id_reused_by_a_protected_process_is_not_a_live_host()
    {
        // Process 4 is the System process: it exists, and opening it is refused, which used to crash startup.
        Assert.False(SessionStore.IsHostAlive(new SessionRecord { Id = SessionStore.NewId(), HostPid = 4 }));
        Assert.False(SessionStore.IsHostAlive(new SessionRecord { Id = SessionStore.NewId(), HostPid = Environment.ProcessId }));
    }
}
