using AgentHub.Hosting;

namespace AgentHub.Terminal;

/// <summary>A process on a pseudo-console that AgentHub owns itself. It dies with AgentHub; the fallback when no host can run.</summary>
public sealed class LocalSession : ITerminalSession
{
    private readonly ConPtySession _inner;

    private LocalSession(ConPtySession inner)
    {
        _inner = inner;
        _inner.OutputDataReceived += text => OutputDataReceived?.Invoke(text, -1);
        _inner.ProcessExited += code => ProcessExited?.Invoke(code);
    }

    public static LocalSession Start(string commandLine, string workingDirectory, int cols, int rows) =>
        new(ConPtySession.Start(commandLine, workingDirectory, cols, rows));

    public bool IsRunning => _inner.IsRunning;
    public int ProcessId => _inner.ProcessId;
    public string? HostedId => null;

    public event Action<string, long>? OutputDataReceived;
    public event Action<int>? ProcessExited;

    public void Write(string data) => _inner.Write(data);
    public void Resize(int cols, int rows) => _inner.Resize(cols, rows);
    public void Kill() => _inner.Kill();
    public void Detach() => Dispose();
    public void Dispose() => _inner.Dispose();
}
