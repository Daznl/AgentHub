using System.IO.Pipes;
using System.Net.Sockets;
using System.Runtime.Versioning;

namespace AgentHub.Hosting;

/// <summary>
/// How a session host and its client reach each other, found from the session id alone. Windows: the named pipe
/// <c>agenthub-session-&lt;id&gt;</c>, as before. Linux: the Unix socket <c>$XDG_RUNTIME_DIR/agenthub/&lt;id&gt;.sock</c>, in a
/// folder only this user can open. Both carry the same <see cref="SessionFrame"/> stream and serve one client at a time:
/// the host listens only while it waits for a client, so a second one waits or times out, as with one pipe instance.
/// </summary>
public static class SessionTransport
{
    /// <summary>Where the host listens, for logs: the pipe name or the socket path.</summary>
    public static string Address(string id) =>
        OperatingSystem.IsLinux() ? SocketPath(id) : SessionStore.PipeName(id);

    /// <summary>
    /// The folder for the Linux sockets: under <c>$XDG_RUNTIME_DIR</c> (per user, cleared at logout) when it is set, otherwise
    /// <c>/tmp/agenthub-&lt;uid&gt;</c>. Either way made with mode 0700, and refused when that cannot be set (another user's folder).
    /// </summary>
    [SupportedOSPlatform("linux")]
    public static string SocketDirectory
    {
        get
        {
            var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
            return !string.IsNullOrWhiteSpace(runtime)
                ? Path.Combine(runtime, "agenthub")
                : Path.Combine("/tmp", "agenthub-" + UnixNative.geteuid());
        }
    }

    [SupportedOSPlatform("linux")]
    public static string SocketPath(string id) => Path.Combine(SocketDirectory, id + ".sock");

    /// <summary>Waits for the next client and returns its connection. Cancelling stops the wait.</summary>
    public static async Task<Stream> AcceptAsync(string id, CancellationToken cancellation)
    {
        if (OperatingSystem.IsWindows())
        {
            var server = new NamedPipeServerStream(SessionStore.PipeName(id), PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            try
            {
                await server.WaitForConnectionAsync(cancellation).ConfigureAwait(false);
                return server;
            }
            catch
            {
                await server.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Session hosts run on Windows and Linux only.");

        var path = SocketPath(id);
        PrepareSocketDirectory();
        TryDelete(path); // a socket left by a host that died
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            listener.Bind(new UnixDomainSocketEndPoint(path));
            listener.Listen(1);
            var client = await listener.AcceptAsync(cancellation).ConfigureAwait(false);
            return new NetworkStream(client, ownsSocket: true);
        }
        finally
        {
            TryDelete(path); // not listening between clients
        }
    }

    /// <summary>
    /// Connects to the session's host, retrying until <paramref name="timeout"/> while it is not listening (starting, or
    /// serving another client). Throws <see cref="TimeoutException"/> when it never answers.
    /// </summary>
    public static async Task<Stream> ConnectAsync(string id, TimeSpan timeout, CancellationToken cancellation)
    {
        if (OperatingSystem.IsWindows())
        {
            var pipe = new NamedPipeClientStream(".", SessionStore.PipeName(id), PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync((int)timeout.TotalMilliseconds, cancellation).ConfigureAwait(false);
                return pipe;
            }
            catch
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Session hosts run on Windows and Linux only.");

        var endpoint = new UnixDomainSocketEndPoint(SocketPath(id));
        var deadline = DateTime.UtcNow + timeout;
        SocketException? last = null;
        while (true)
        {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                await socket.ConnectAsync(endpoint, cancellation).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException ex)
            {
                socket.Dispose();
                last = ex;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException($"The session host did not answer at {endpoint} ({last.Message}).", last);
            await Task.Delay(50, cancellation).ConfigureAwait(false);
        }
    }

    [SupportedOSPlatform("linux")]
    private static void PrepareSocketDirectory()
    {
        var dir = SocketDirectory;
        const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        Directory.CreateDirectory(dir, OwnerOnly);
        if (File.GetUnixFileMode(dir) != OwnerOnly) File.SetUnixFileMode(dir, OwnerOnly); // throws for another user's folder
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
