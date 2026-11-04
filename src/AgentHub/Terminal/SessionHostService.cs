using System.Diagnostics;
using System.Security.Cryptography;
using AgentHub.Hosting;

namespace AgentHub.Terminal;

/// <summary>
/// Starts session hosts and finds the ones still running. The host executable ships beside AgentHub.exe but runs from
/// <c>C:\data\agenthub\host\&lt;version&gt;\</c>, a copy keyed by its own bytes: a publish over the live folder then never
/// touches a running host, and an old host keeps its old code while a newer AgentHub attaches to it.
/// </summary>
public sealed class SessionHostService
{
    public const string DefaultInstallRoot = @"C:\data\agenthub\host";
    private const string HostFileName = "AgentHub.SessionHost";
    private static readonly string[] HostFiles =
    [
        HostFileName + ".exe", HostFileName + ".dll", HostFileName + ".runtimeconfig.json", HostFileName + ".deps.json", HostFileName + ".pdb",
        "AgentHub.Hosting.dll", "AgentHub.Hosting.pdb"
    ];

    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan AttachTimeout = TimeSpan.FromSeconds(3);

    private readonly string _installRoot;
    private readonly Lazy<string?> _hostExecutable;

    public SessionHostService(string? sessionsRoot = null, string? installRoot = null, string? bundledDirectory = null)
    {
        Store = new SessionStore(sessionsRoot);
        _installRoot = string.IsNullOrWhiteSpace(installRoot) ? DefaultInstallRoot : installRoot;
        var bundled = bundledDirectory ?? AppContext.BaseDirectory;
        _hostExecutable = new Lazy<string?>(() => Install(bundled));
    }

    public SessionStore Store { get; }

    /// <summary>Path of the host executable to run, or null when AgentHub was built without it.</summary>
    public string? HostExecutable => _hostExecutable.Value;

    public bool IsAvailable => HostExecutable is not null;

    /// <summary>Why the host is unavailable, for the pane subtitle; null when it is.</summary>
    public string? UnavailableReason { get; private set; }

    /// <summary>Writes the record, starts a host for it and attaches. Throws when the host cannot start or run the command.</summary>
    public async Task<HostedSession> StartAsync(SessionRecord record, CancellationToken cancellation = default)
    {
        var exe = HostExecutable ?? throw new InvalidOperationException(UnavailableReason ?? "The session host is not available.");
        record.HostPid = 0;
        record.ProcessId = 0;
        record.ExitCode = null;
        record.Error = null;
        Store.Save(record);

        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(exe)!
        };
        start.ArgumentList.Add(record.Id);
        start.ArgumentList.Add("--root");
        start.ArgumentList.Add(Store.Root);

        using var host = Process.Start(start) ?? throw new InvalidOperationException("The session host process did not start.");

        var deadline = DateTimeOffset.Now + StartTimeout;
        Exception? last = null;
        while (DateTimeOffset.Now < deadline)
        {
            cancellation.ThrowIfCancellationRequested();
            if (host.HasExited)
            {
                var failed = Store.Load(record.Id);
                throw new InvalidOperationException(failed?.Error ?? $"The session host exited with code {host.ExitCode} before the session started.");
            }
            try
            {
                var session = await HostedSession.AttachAsync(record, TimeSpan.FromMilliseconds(500), -1, cancellation).ConfigureAwait(false);
                // The host wrote its own process id and the CLI's; carry them so the layout can tell whether it is alive.
                if (Store.Load(record.Id) is { } saved)
                {
                    record.HostPid = saved.HostPid;
                    record.ProcessId = saved.ProcessId;
                }
                return session;
            }
            catch (TimeoutException ex) { last = ex; }
            catch (IOException ex) { last = ex; }
            await Task.Delay(100, cancellation).ConfigureAwait(false);
        }
        throw new TimeoutException("The session host did not answer in time.", last);
    }

    /// <summary>
    /// Attaches to a running host. Throws when it is gone, busy with another AgentHub, or speaks another protocol. With
    /// <paramref name="fromOffset"/> (the stream offset a saved screen stands at) the host replays only what came after it.
    /// </summary>
    public Task<HostedSession> AttachAsync(SessionRecord record, long fromOffset = -1, CancellationToken cancellation = default) =>
        HostedSession.AttachAsync(record, AttachTimeout, fromOffset, cancellation);

    /// <summary>Records whose host process still runs, newest first.</summary>
    public List<SessionRecord> ListLive() => Store.ListAll().Where(SessionStore.IsHostAlive).ToList();

    /// <summary>Removes the folders of sessions whose host is gone, keeping the newest <paramref name="keep"/> for their replays.</summary>
    public void PruneEnded(int keep = 20)
    {
        var ended = Store.ListAll().Where(r => !SessionStore.IsHostAlive(r)).Skip(keep).ToList();
        foreach (var record in ended)
        {
            try { Store.Delete(record.Id); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private string? Install(string bundled)
    {
        var source = Path.Combine(bundled, HostFileName + ".exe");
        if (!File.Exists(source))
        {
            UnavailableReason = $"{HostFileName}.exe is not beside AgentHub.exe; sessions run inside AgentHub and end with it.";
            return null;
        }

        try
        {
            var version = VersionKey(bundled);
            var dir = Path.Combine(_installRoot, version);
            var target = Path.Combine(dir, HostFileName + ".exe");
            if (!File.Exists(target))
            {
                var staging = dir + ".staging-" + Environment.ProcessId;
                Directory.CreateDirectory(staging);
                foreach (var name in HostFiles)
                {
                    var file = Path.Combine(bundled, name);
                    if (File.Exists(file)) File.Copy(file, Path.Combine(staging, name), overwrite: true);
                }
                try { Directory.Move(staging, dir); }
                catch (IOException) when (Directory.Exists(dir)) { Directory.Delete(staging, recursive: true); } // another instance got there first
            }
            return target;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // No versioned copy possible (folder not writable): run the bundled one. A publish while a host runs will
            // rename it aside, which the running host survives.
            UnavailableReason = null;
            return source;
        }
    }

    /// <summary>A short hash of the host's own code, so each build of it gets its own folder.</summary>
    private static string VersionKey(string bundled)
    {
        using var sha = SHA256.Create();
        foreach (var name in new[] { HostFileName + ".dll", "AgentHub.Hosting.dll" })
        {
            var path = Path.Combine(bundled, name);
            if (!File.Exists(path)) continue;
            var bytes = File.ReadAllBytes(path);
            sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
        }
        sha.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(sha.Hash!)[..12].ToLowerInvariant();
    }
}
