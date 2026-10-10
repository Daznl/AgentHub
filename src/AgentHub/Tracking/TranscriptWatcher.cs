using System.Text;

namespace AgentHub.Tracking;

/// <summary>
/// Follows one Claude Code session's transcript file as it grows, reading only the bytes added since
/// the last poll, and raises a <see cref="TurnEvent"/> each time a prompt starts or a turn finishes.
/// Events are raised on a thread-pool thread.
/// </summary>
public sealed class TranscriptWatcher : IDisposable
{
    // Short enough that a prompt shows on the pane about as soon as it is submitted; a poll with nothing new is one file-length check.
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);
    // Until the transcript exists, the folder-wide fallback search runs only every this many polls.
    private const int SearchEveryPolls = 20;

    private readonly string _sessionId;
    private readonly string _workingDirectory;
    private readonly string? _configDirectory;
    private readonly ClaudeTranscriptParser _parser = new();
    private readonly object _gate = new();
    private readonly System.Threading.Timer _timer;
    private string? _path;
    private long _offset;
    private byte[] _partialLine = [];
    private int _lookups;
    private bool _disposed;

    public event Action<TurnEvent>? TurnEvent;

    public string SessionId => _sessionId;

    public TranscriptWatcher(string sessionId, string workingDirectory, string? configDirectory = null)
    {
        _sessionId = sessionId;
        _workingDirectory = workingDirectory;
        _configDirectory = configDirectory;
        _timer = new System.Threading.Timer(_ => Poll(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public void Start() => _timer.Change(TimeSpan.Zero, PollInterval);

    /// <summary>Reads anything new and completes a turn whose closing text has no stop marker. Call when the agent goes quiet.</summary>
    public void Settle() => ThreadPool.QueueUserWorkItem(_ => Run(settle: true));

    /// <summary>Synchronous read of everything new. Exposed for tests.</summary>
    public void Poll() => Run(settle: false);

    private void Run(bool settle)
    {
        // A slow read (large tool output) must not stack up timer callbacks; a settle waits its turn.
        if (settle) Monitor.Enter(_gate);
        else if (!Monitor.TryEnter(_gate)) return;
        var events = new List<TurnEvent>();
        try
        {
            if (_disposed) return;
            ReadNewLines(events);
            if (settle) events.AddRange(_parser.Settle());
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        finally
        {
            Monitor.Exit(_gate);
        }

        foreach (var e in events)
            TurnEvent?.Invoke(e);
    }

    private void ReadNewLines(List<TurnEvent> events)
    {
        _path ??= ClaudeSessionLaunch.FindTranscript(_sessionId, _workingDirectory, _configDirectory,
            searchAll: _lookups++ % SearchEveryPolls == 0);
        if (_path is null) return;

        using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length < _offset)
        {
            // Truncated or replaced: start again from the top.
            _offset = 0;
            _partialLine = [];
        }
        if (stream.Length == _offset) return;

        stream.Seek(_offset, SeekOrigin.Begin);
        var fresh = new byte[stream.Length - _offset];
        var read = 0;
        while (read < fresh.Length)
        {
            var n = stream.Read(fresh, read, fresh.Length - read);
            if (n == 0) break;
            read += n;
        }
        _offset += read;

        var buffer = _partialLine.Length == 0 ? fresh.AsSpan(0, read) : _partialLine.Concat(fresh.Take(read)).ToArray().AsSpan();
        var start = 0;
        for (var i = 0; i < buffer.Length; i++)
        {
            if (buffer[i] != (byte)'\n') continue;
            var line = Encoding.UTF8.GetString(buffer[start..i]);
            events.AddRange(_parser.Feed(line));
            start = i + 1;
        }
        // Keep an unfinished last line until the rest of it is written.
        _partialLine = buffer[start..].ToArray();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
        }
        _timer.Dispose();
    }
}
