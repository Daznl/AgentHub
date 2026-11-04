using System.IO;
using System.Diagnostics;
using System.Text;
using AgentHub.Hosting;
using AgentHub.Terminal;

namespace AgentHub.Tests;

/// <summary>
/// Runs a real AgentHub.SessionHost (the one built beside this test assembly) with PowerShell inside, in a folder whose
/// path has spaces: attach, type, detach, reattach and see the replay, exit, and find the record and replay on disk.
/// </summary>
public sealed class SessionHostLiveTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "agenthub host test " + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static async Task<string> WaitForAsync(Func<string> text, string marker, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var current = text();
            if (current.Contains(marker, StringComparison.Ordinal)) return current;
            await Task.Delay(100);
        }
        throw new TimeoutException($"'{marker}' did not appear within {timeout}. Output so far:\n{text()}");
    }

    [LiveFact("powershell")]
    public async Task Session_survives_detach_and_replays_on_reattach()
    {
        var sessions = Path.Combine(_root, "sessions here");
        var install = Path.Combine(_root, "host install");
        var work = Path.Combine(_root, "work folder");
        Directory.CreateDirectory(work);

        var service = new SessionHostService(sessions, install, AppContext.BaseDirectory);
        Assert.True(service.IsAvailable, service.UnavailableReason);
        Assert.StartsWith(install, service.HostExecutable!, StringComparison.OrdinalIgnoreCase);

        var record = new SessionRecord
        {
            Id = SessionStore.NewId(),
            CommandLine = "powershell.exe -NoLogo -NoProfile",
            WorkingDirectory = work,
            Title = "test",
            SessionName = "work folder",
            Cols = 100,
            Rows = 30,
            StartedAt = DateTimeOffset.Now
        };

        // First attach: type something and see it echoed by the shell.
        var first = await service.StartAsync(record);
        var output = new StringBuilder();
        var exited = new TaskCompletionSource<int>();
        first.OutputDataReceived += text => { lock (output) output.Append(text); };
        first.ProcessExited += code => exited.TrySetResult(code);
        first.Start();
        Assert.True(first.IsRunning);
        Assert.True(record.HostPid > 0);
        Assert.True(SessionStore.IsHostAlive(record));

        first.Write("echo marker-one-$(1+1)\r");
        string Seen() { lock (output) return output.ToString(); }
        await WaitForAsync(Seen, "marker-one-2", TimeSpan.FromSeconds(30));

        // Detach: the host and the shell keep running.
        first.Detach();
        await Task.Delay(500);
        Assert.True(SessionStore.IsHostAlive(record));
        Assert.False(exited.Task.IsCompleted);

        // Reattach: the replay holds what was typed before, then live output continues.
        var second = await service.AttachAsync(record);
        Assert.Contains("marker-one-2", second.ReplayText, StringComparison.Ordinal);
        Assert.True(second.IsRunning);
        var output2 = new StringBuilder();
        var exited2 = new TaskCompletionSource<int>();
        second.OutputDataReceived += text => { lock (output2) output2.Append(text); };
        second.ProcessExited += code => exited2.TrySetResult(code);
        second.Start();
        second.Resize(120, 40);
        second.Write("echo marker-two-$(2+2)\r");
        string Seen2() { lock (output2) return output2.ToString(); }
        await WaitForAsync(Seen2, "marker-two-4", TimeSpan.FromSeconds(30));

        // Exit: the client hears it, the host records it and saves the replay, then stops.
        second.Write("exit\r");
        var code = await exited2.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(0, code);
        second.Dispose();

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (SessionStore.IsHostAlive(record) && DateTime.UtcNow < deadline) await Task.Delay(100);
        Assert.False(SessionStore.IsHostAlive(record));

        var saved = service.Store.Load(record.Id);
        Assert.NotNull(saved);
        Assert.Equal(0, saved!.ExitCode);
        Assert.NotNull(saved.ExitedAt);
        Assert.Equal((120, 40), (saved.Cols, saved.Rows));
        var replay = Encoding.UTF8.GetString(File.ReadAllBytes(service.Store.ReplayPath(record.Id)));
        Assert.Contains("marker-one-2", replay, StringComparison.Ordinal);
        Assert.Contains("marker-two-4", replay, StringComparison.Ordinal);
        Assert.Empty(service.ListLive());
    }

    [LiveFact("powershell")]
    public async Task Kill_ends_the_process_and_the_host()
    {
        var service = new SessionHostService(Path.Combine(_root, "s"), Path.Combine(_root, "i"), AppContext.BaseDirectory);
        var record = new SessionRecord
        {
            Id = SessionStore.NewId(),
            CommandLine = "powershell.exe -NoLogo -NoProfile",
            WorkingDirectory = _root,
            StartedAt = DateTimeOffset.Now
        };
        var session = await service.StartAsync(record);
        var exited = new TaskCompletionSource<int>();
        session.ProcessExited += code => exited.TrySetResult(code);
        session.Start();

        session.Kill();
        var code = await exited.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.NotEqual(0, code);
        Assert.False(session.HostLost);
        session.Dispose();

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (SessionStore.IsHostAlive(record) && DateTime.UtcNow < deadline) await Task.Delay(100);
        Assert.False(SessionStore.IsHostAlive(record));
        Assert.Equal(code, service.Store.Load(record.Id)!.ExitCode);
    }

    [LiveFact("powershell")]
    public async Task A_command_that_cannot_start_reports_why()
    {
        var service = new SessionHostService(Path.Combine(_root, "s"), Path.Combine(_root, "i"), AppContext.BaseDirectory);
        var record = new SessionRecord
        {
            Id = SessionStore.NewId(),
            CommandLine = "no-such-program-" + Guid.NewGuid().ToString("N") + ".exe",
            WorkingDirectory = _root,
            StartedAt = DateTimeOffset.Now
        };
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(record));
        Assert.Contains("CreateProcess failed", ex.Message, StringComparison.Ordinal);
        Assert.NotNull(service.Store.Load(record.Id)!.Error);
    }

    [LiveFact("powershell")]
    public async Task Attaching_to_a_session_whose_host_is_gone_fails_fast()
    {
        var service = new SessionHostService(Path.Combine(_root, "s"), Path.Combine(_root, "i"), AppContext.BaseDirectory);
        var record = new SessionRecord { Id = SessionStore.NewId(), HostPid = 0 };
        var watch = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<Exception>(() => service.AttachAsync(record));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"took {watch.Elapsed}");
    }
}
