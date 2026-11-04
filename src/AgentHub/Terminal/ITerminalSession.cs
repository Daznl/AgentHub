namespace AgentHub.Terminal;

/// <summary>
/// What a terminal pane needs from the process behind it, whether that process runs in AgentHub (<see cref="LocalSession"/>)
/// or in a session host that outlives it (<see cref="HostedSession"/>). Events arrive on background threads.
/// </summary>
public interface ITerminalSession : IDisposable
{
    bool IsRunning { get; }
    int ProcessId { get; }

    /// <summary>Hosted session id, or null for a process AgentHub runs itself.</summary>
    string? HostedId { get; }

    /// <summary>Decoded output text, in order.</summary>
    event Action<string>? OutputDataReceived;

    /// <summary>The process ended with this exit code. -1 when the host was lost rather than the process ending.</summary>
    event Action<int>? ProcessExited;

    void Write(string data);
    void Resize(int cols, int rows);

    /// <summary>Ends the process. Explicit user action only.</summary>
    void Kill();

    /// <summary>
    /// Lets go of the session without ending it. A hosted process keeps running for a later attach; a local one has
    /// nowhere to go and is killed. Called when AgentHub closes.
    /// </summary>
    void Detach();
}
