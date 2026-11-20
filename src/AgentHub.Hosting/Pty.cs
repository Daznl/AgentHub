namespace AgentHub.Hosting;

/// <summary>A process on a pseudo-terminal: a Windows pseudo-console (<see cref="ConPtySession"/>) or a Linux pseudo-terminal (<see cref="UnixPty"/>).</summary>
public interface IPty : IDisposable
{
    int ProcessId { get; }
    bool IsRunning { get; }
    void Write(byte[] bytes);
    void Resize(int cols, int rows);

    /// <summary>Ends the process. Explicit user action only.</summary>
    void Kill();
}

public static class Pty
{
    /// <summary>
    /// Starts <paramref name="commandLine"/> on this system's pseudo-terminal, with <paramref name="onOutput"/> getting each
    /// piece of output as raw bytes and <paramref name="onExited"/> the exit code. Throws when the command cannot start.
    /// On Windows the command line goes to <c>CreateProcess</c>; on Linux to <c>/bin/sh -c</c>.
    /// </summary>
    public static IPty Start(string commandLine, string workingDirectory, int cols, int rows, Action<byte[]> onOutput, Action<int> onExited)
    {
        if (OperatingSystem.IsWindows())
        {
            var session = ConPtySession.Start(commandLine, workingDirectory, cols, rows);
            session.OutputBytesReceived += onOutput;
            session.ProcessExited += onExited;
            return session;
        }
        if (OperatingSystem.IsLinux()) return UnixPty.Start(commandLine, workingDirectory, cols, rows, onOutput, onExited);
        throw new PlatformNotSupportedException("AgentHub hosts sessions on Windows and Linux only.");
    }
}
