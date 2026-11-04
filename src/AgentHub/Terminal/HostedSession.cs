using System.IO.Pipes;
using System.Text;
using System.Threading.Channels;
using AgentHub.Hosting;

namespace AgentHub.Terminal;

/// <summary>
/// A session running in an <c>AgentHub.SessionHost</c> process, reached over its named pipe. Attaching yields the host's
/// replay of everything the process wrote so far (<see cref="ReplayText"/>); <see cref="Start"/> then streams live output.
/// Detaching leaves the process running for the next attach; only <see cref="Kill"/> ends it.
/// </summary>
public sealed class HostedSession : ITerminalSession
{
    private readonly NamedPipeClientStream _pipe;
    private readonly Channel<SessionFrame> _outgoing = Channel.CreateUnbounded<SessionFrame>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private readonly object _lock = new();
    private Task? _pump;
    private bool _released;
    private volatile bool _running;

    private HostedSession(NamedPipeClientStream pipe, SessionRecord record, WelcomeInfo welcome, byte[] replay)
    {
        _pipe = pipe;
        Record = record;
        Welcome = welcome;
        _running = welcome.Running;
        ReplayText = Decode(replay);
    }

    public SessionRecord Record { get; }
    public WelcomeInfo Welcome { get; }

    /// <summary>What the process had written before this attach, decoded. Write it to the terminal before <see cref="Start"/>.</summary>
    public string ReplayText { get; }

    /// <summary>True when the pipe broke without the host saying the process exited: the host itself is gone.</summary>
    public bool HostLost { get; private set; }

    public bool IsRunning => _running;
    public int ProcessId => Welcome.ProcessId;
    public string? HostedId => Record.Id;

    public event Action<string>? OutputDataReceived;
    public event Action<int>? ProcessExited;

    /// <summary>Connects to the session's pipe, exchanges hello and welcome, and reads the replay. Throws when refused or absent.</summary>
    public static async Task<HostedSession> AttachAsync(SessionRecord record, TimeSpan timeout, CancellationToken cancellation = default)
    {
        var pipe = new NamedPipeClientStream(".", SessionStore.PipeName(record.Id), PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync((int)timeout.TotalMilliseconds, cancellation).ConfigureAwait(false);
            await SessionFrame.Hello(Environment.ProcessId).WriteAsync(pipe, cancellation).ConfigureAwait(false);

            var first = await SessionFrame.ReadAsync(pipe, cancellation).ConfigureAwait(false)
                        ?? throw new IOException("The session host closed the pipe before answering.");
            if (first.Type == FrameType.Refused) throw new InvalidOperationException(SessionFrame.ReadText(first));
            if (first.Type != FrameType.Welcome) throw new InvalidDataException($"Expected a welcome from the session host, got {first.Type}.");
            var welcome = SessionFrame.ReadWelcome(first);
            if (welcome.Version != SessionFrame.ProtocolVersion)
                throw new InvalidOperationException($"The session host speaks protocol {welcome.Version}; this AgentHub speaks {SessionFrame.ProtocolVersion}.");

            var replay = await SessionFrame.ReadAsync(pipe, cancellation).ConfigureAwait(false)
                         ?? throw new IOException("The session host closed the pipe before sending the replay.");
            if (replay.Type != FrameType.Replay) throw new InvalidDataException($"Expected the replay from the session host, got {replay.Type}.");

            return new HostedSession(pipe, record, welcome, replay.Payload);
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Begins streaming live output and exit. Subscribe to the events first.</summary>
    public void Start()
    {
        lock (_lock)
        {
            if (_pump is not null || _released) return;
            _pump = Task.Run(PumpOutgoingAsync);
        }
        _ = Task.Run(ReadIncomingAsync);
    }

    public void Write(string data)
    {
        if (data.Length == 0) return;
        Send(SessionFrame.Input(data));
    }

    public void Resize(int cols, int rows) => Send(SessionFrame.Resize(cols, rows));

    public void Kill() => Send(SessionFrame.Kill());

    public void Detach()
    {
        lock (_lock)
        {
            if (_released) return;
            _released = true;
        }
        _outgoing.Writer.TryWrite(SessionFrame.Detach());
        _outgoing.Writer.TryComplete();
        // The pump sends the detach, then the pipe closes; reads end with the pipe. Without a pump (never started) close now.
        if (_pump is null) ClosePipe();
    }

    public void Dispose() => Detach();

    private void Send(SessionFrame frame)
    {
        lock (_lock)
        {
            if (_released) return;
        }
        _outgoing.Writer.TryWrite(frame);
    }

    private async Task PumpOutgoingAsync()
    {
        try
        {
            await foreach (var frame in _outgoing.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                await frame.WriteAsync(_pipe).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The host went away; the read loop reports it.
        }
        finally
        {
            ClosePipe();
        }
    }

    private async Task ReadIncomingAsync()
    {
        try
        {
            while (true)
            {
                var frame = await SessionFrame.ReadAsync(_pipe).ConfigureAwait(false);
                if (frame is null) break;
                switch (frame.Type)
                {
                    case FrameType.Output:
                        var text = Decode(frame.Payload);
                        if (text.Length > 0) OutputDataReceived?.Invoke(text);
                        break;
                    case FrameType.Exited:
                        _running = false;
                        ProcessExited?.Invoke(SessionFrame.ReadExited(frame));
                        return;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidDataException)
        {
        }

        // Pipe ended without an exit: our own detach, or the host died under us.
        bool released;
        lock (_lock) released = _released;
        if (!released && _running)
        {
            HostLost = true;
            _running = false;
            ProcessExited?.Invoke(-1);
        }
    }

    private string Decode(byte[] bytes)
    {
        if (bytes.Length == 0) return "";
        var chars = new char[_decoder.GetCharCount(bytes, 0, bytes.Length, flush: false)];
        var count = _decoder.GetChars(bytes, 0, bytes.Length, chars, 0, flush: false);
        return new string(chars, 0, count);
    }

    private void ClosePipe()
    {
        try { _pipe.Dispose(); } catch { }
    }
}
