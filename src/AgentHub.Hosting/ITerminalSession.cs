namespace AgentHub.Hosting;

/// <summary>
/// What a terminal pane needs from the process behind it, whether that process runs in AgentHub (the Windows app's <c>LocalSession</c>)
/// or in a session host that outlives it (<see cref="HostedSession"/>). Events arrive on background threads.
/// </summary>
public interface ITerminalSession : IDisposable
{
    bool IsRunning { get; }
    int ProcessId { get; }

    /// <summary>Hosted session id, or null for a process AgentHub runs itself.</summary>
    string? HostedId { get; }

    /// <summary>
    /// Decoded output text, in order, with the session's stream offset after it (how many bytes the process has written up
    /// to and including this piece; -1 when the session does not count). A piece may be empty when it only completed a
    /// character split across two reads; the offset still moves.
    /// </summary>
    event Action<string, long>? OutputDataReceived;

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
