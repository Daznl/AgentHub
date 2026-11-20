using System.Runtime.InteropServices;
using System.Threading.Channels;
using AgentHub.Hosting;

namespace AgentHub.SessionHost;

/// <summary>
/// <c>AgentHub.SessionHost &lt;session id&gt; [--root &lt;sessions folder&gt;]</c>. Reads the session record AgentHub wrote,
/// runs its command on a pseudo-terminal (Windows pseudo-console, Linux pseudo-terminal), keeps the last bytes of output,
/// and serves one client at a time on the session's pipe or socket (<see cref="SessionTransport"/>). Lives exactly as long
/// as the CLI: when that exits, the host records the exit code, keeps the replay on disk, tells the attached client if
/// any, and stops. AgentHub closing is a detach, nothing more.
/// </summary>
public static class Program
{
    private const int ClientGraceSeconds = 5;

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("usage: AgentHub.SessionHost <session id> [--root <sessions folder>]");
            return 2;
        }

        // Linux: leave the starter's session and process group, so a Ctrl+C or hang-up meant for whoever started the
        // host (a terminal, the daemon) never reaches it. Windows hosts already run apart from AgentHub.
        if (OperatingSystem.IsLinux()) setsid();

        var id = args[0];
        string? root = null;
        for (var i = 1; i + 1 < args.Length; i++)
            if (args[i] == "--root") root = args[i + 1];

        var store = new SessionStore(root);
        var record = store.Load(id);
        if (record is null)
        {
            Console.Error.WriteLine($"No session record at {store.RecordPath(id)}");
            return 2;
        }

        var host = new Host(store, record);
        return await host.RunAsync();
    }

    [DllImport("libc")]
    private static extern int setsid();

    private sealed class Host
    {
        private readonly SessionStore _store;
        private readonly SessionRecord _record;
        private readonly ReplayBuffer _buffer = new();
        private readonly object _lock = new();
        private Channel<SessionFrame>? _client;
        private IPty? _session;
        private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _cols;
        private int _rows;

        public Host(SessionStore store, SessionRecord record)
        {
            _store = store;
            _record = record;
            _cols = record.Cols;
            _rows = record.Rows;
        }

        public async Task<int> RunAsync()
        {
            Log($"host {Environment.ProcessId} starting: {_record.CommandLine} in {_record.WorkingDirectory} ({_cols}x{_rows})");
            try
            {
                _session = Pty.Start(_record.CommandLine, _record.WorkingDirectory, _cols, _rows, OnOutput, OnExited);
            }
            catch (Exception ex)
            {
                Log("launch failed: " + ex.Message);
                _record.HostPid = Environment.ProcessId;
                _record.Error = ex.Message;
                _record.ExitedAt = DateTimeOffset.Now;
                _store.Save(_record);
                return 1;
            }

            _record.HostPid = Environment.ProcessId;
            _record.ProcessId = _session.ProcessId;
            _store.Save(_record);
            Log($"process {_session.ProcessId} running; listening at {SessionTransport.Address(_record.Id)}");

            try
            {
                while (true)
                {
                    using var cancelWait = new CancellationTokenSource();
                    var connect = SessionTransport.AcceptAsync(_record.Id, cancelWait.Token);
                    var done = await Task.WhenAny(connect, _exited.Task);
                    if (done != connect)
                    {
                        cancelWait.Cancel();
                        try { (await connect).Dispose(); }
                        catch (Exception ex) when (ex is OperationCanceledException or IOException or System.Net.Sockets.SocketException) { }
                        break; // the CLI ended while nobody was attached
                    }

                    await using var client = await connect;
                    await ServeAsync(client);
                    if (_exited.Task.IsCompleted) break;
                }
            }
            catch (Exception ex)
            {
                Log("host loop failed: " + ex);
            }

            var exitCode = _exited.Task.IsCompleted ? _exited.Task.Result : -1;
            Finish(exitCode);
            return 0;
        }

        private void OnOutput(byte[] bytes)
        {
            lock (_lock)
            {
                _buffer.Append(bytes);
                _client?.Writer.TryWrite(SessionFrame.Output(bytes));
            }
        }

        private void OnExited(int exitCode)
        {
            Log($"process exited with code {exitCode}");
            lock (_lock)
            {
                _client?.Writer.TryWrite(SessionFrame.Exited(exitCode));
                _client?.Writer.TryComplete();
            }
            _exited.TrySetResult(exitCode);
        }

        /// <summary>Talks to one attached client until it detaches, drops off, or has been told the CLI exited.</summary>
        private async Task ServeAsync(Stream pipe)
        {
            var channel = Channel.CreateUnbounded<SessionFrame>(new UnboundedChannelOptions { SingleReader = true });
            try
            {
                var hello = await SessionFrame.ReadAsync(pipe);
                if (hello is null || hello.Type != FrameType.Hello)
                {
                    Log("client sent no hello; dropped");
                    return;
                }
                var (version, clientPid, fromOffset) = SessionFrame.ReadHello(hello);
                if (version != SessionFrame.ProtocolVersion)
                {
                    Log($"client {clientPid} speaks protocol {version}, host speaks {SessionFrame.ProtocolVersion}; refused");
                    await SessionFrame.Refused($"This session host speaks protocol {SessionFrame.ProtocolVersion}; the client speaks {version}.").WriteAsync(pipe);
                    return;
                }

                // Snapshot and hand-over happen under the same lock as output appends, so nothing is lost or sent twice.
                ReplaySlice replay;
                lock (_lock)
                {
                    var running = !_exited.Task.IsCompleted;
                    var exitCode = running ? 0 : _exited.Task.Result;
                    replay = _buffer.SnapshotFrom(fromOffset);
                    channel.Writer.TryWrite(SessionFrame.Welcome(_record.ProcessId, running, exitCode, _cols, _rows, _buffer.TotalAppended, replay.Offset));
                    channel.Writer.TryWrite(SessionFrame.Replay(replay.Bytes));
                    if (!running)
                    {
                        channel.Writer.TryWrite(SessionFrame.Exited(exitCode));
                        channel.Writer.TryComplete();
                    }
                    _client = channel;
                }
                Log(fromOffset >= 0 && replay.Offset == fromOffset
                    ? $"client {clientPid} attached; replaying from its offset {fromOffset} ({replay.Bytes.Length} bytes)"
                    : $"client {clientPid} attached; replaying the ring from {replay.Offset} ({replay.Bytes.Length} bytes)");

                var writer = PumpAsync(channel.Reader, pipe);
                var reader = ReadClientAsync(pipe);

                // Serving ends when the client leaves, or shortly after it has been told the CLI exited.
                var first = await Task.WhenAny(reader, writer);
                if (first == writer && !reader.IsCompleted)
                    await Task.WhenAny(reader, Task.Delay(TimeSpan.FromSeconds(ClientGraceSeconds)));
                lock (_lock) { if (ReferenceEquals(_client, channel)) _client = null; }
                channel.Writer.TryComplete();
                try { await writer; } catch (Exception ex) { Log("writer: " + ex.Message); }
                Log($"client {clientPid} gone");
            }
            catch (Exception ex)
            {
                Log("serve failed: " + ex.Message);
            }
            finally
            {
                lock (_lock) { if (ReferenceEquals(_client, channel)) _client = null; }
                try
                {
                    if (pipe is System.IO.Pipes.NamedPipeServerStream server) server.Disconnect();
                    else pipe.Dispose();
                }
                catch { }
            }
        }

        private static async Task PumpAsync(ChannelReader<SessionFrame> frames, Stream pipe)
        {
            await foreach (var frame in frames.ReadAllAsync())
            {
                await frame.WriteAsync(pipe);
            }
        }

        private async Task ReadClientAsync(Stream pipe)
        {
            try
            {
                while (true)
                {
                    var frame = await SessionFrame.ReadAsync(pipe);
                    if (frame is null) return; // client disconnected
                    switch (frame.Type)
                    {
                        case FrameType.Input:
                            _session?.Write(frame.Payload);
                            break;
                        case FrameType.Resize:
                            var (cols, rows) = SessionFrame.ReadResize(frame);
                            if (cols != _cols || rows != _rows)
                            {
                                _cols = cols;
                                _rows = rows;
                                _session?.Resize(cols, rows);
                            }
                            break;
                        case FrameType.Kill:
                            Log("client asked to kill the process");
                            _session?.Kill();
                            break;
                        case FrameType.Detach:
                            Log("client detached");
                            return;
                        default:
                            Log($"unexpected frame {frame.Type} from client; ignored");
                            break;
                    }
                }
            }
            catch (IOException ex)
            {
                Log("client pipe broke: " + ex.Message);
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private void Finish(int exitCode)
        {
            var replay = _buffer.SnapshotFrom(-1);
            try
            {
                File.WriteAllBytes(_store.ReplayPath(_record.Id), replay.Bytes);
            }
            catch (Exception ex)
            {
                Log("could not save the replay: " + ex.Message);
            }

            // AgentHub may have added the transcript path meanwhile; keep it.
            if (_store.Load(_record.Id) is { TranscriptPath: { } transcript }) _record.TranscriptPath = transcript;
            _record.ReplayStart = replay.Offset;
            _record.ExitCode = exitCode;
            _record.ExitedAt = DateTimeOffset.Now;
            _record.Cols = _cols;
            _record.Rows = _rows;
            try { _store.Save(_record); } catch (Exception ex) { Log("could not save the record: " + ex.Message); }

            _session?.Dispose();
            Log($"host stopping (exit code {exitCode})");
        }

        private void Log(string line)
        {
            try
            {
                File.AppendAllText(_store.LogPath(_record.Id), $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} {line}{Environment.NewLine}");
            }
            catch
            {
            }
        }
    }
}
