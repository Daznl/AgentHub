using System.Collections;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace AgentHub.Hosting;

/// <summary>
/// A process on a Linux pseudo-terminal, owned by this process: the Linux twin of <see cref="ConPtySession"/>. The command
/// line runs under <c>/bin/sh -c</c> (as a Linux terminal would run it) in a new session whose controlling terminal is the
/// pseudo-terminal, so the CLI sees a real terminal, gets SIGWINCH on resize and SIGHUP when the terminal goes.
/// <para>
/// Started with <c>posix_spawn</c>, never <c>fork</c> from managed code: <c>POSIX_SPAWN_SETSID</c> makes the child a session
/// leader first, then its file actions open the terminal's other side as standard input (which makes it the controlling
/// terminal) and copy it to standard output and error, then change to the working folder.
/// </para>
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class UnixPty : IPty
{
    private const string Shell = "/bin/sh";
    private static readonly TimeSpan OutputDrainWait = TimeSpan.FromSeconds(1);

    private readonly object _writeLock = new();
    private readonly Action<byte[]> _onOutput;
    private readonly Action<int> _onExited;
    private readonly ManualResetEventSlim _readerDone = new(false);
    private int _master = -1;
    private volatile bool _running;
    private volatile bool _disposed;

    private UnixPty(Action<byte[]> onOutput, Action<int> onExited)
    {
        _onOutput = onOutput;
        _onExited = onExited;
    }

    public int ProcessId { get; private set; }
    public bool IsRunning => _running;

    /// <summary>
    /// Starts <paramref name="commandLine"/> in <paramref name="workingDirectory"/>. The callbacks are in place before
    /// anything is read, so no output and no exit is missed; output comes on a reader thread, the exit (after the last
    /// output) on a waiter thread. Throws <see cref="InvalidOperationException"/> when the program or folder is not there.
    /// </summary>
    public static UnixPty Start(string commandLine, string workingDirectory, int cols, int rows, Action<byte[]> onOutput, Action<int> onExited)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) throw new InvalidOperationException("No command to run.");
        if (!string.IsNullOrWhiteSpace(workingDirectory) && !Directory.Exists(workingDirectory))
            throw new InvalidOperationException($"Cannot start '{commandLine}': the folder '{workingDirectory}' does not exist.");
        var program = ShellCommand.FirstProgram(commandLine);
        if (program is not null && ShellCommand.Resolve(program) is null)
            throw new InvalidOperationException($"Cannot start '{commandLine}': '{program}' was not found.");

        var pty = new UnixPty(onOutput, onExited);
        pty.Spawn(commandLine, workingDirectory, cols, rows);
        return pty;
    }

    private void Spawn(string commandLine, string workingDirectory, int cols, int rows)
    {
        _master = UnixNative.posix_openpt(UnixNative.O_RDWR | UnixNative.O_NOCTTY | UnixNative.O_CLOEXEC);
        if (_master < 0) throw Failure("posix_openpt");
        if (UnixNative.grantpt(_master) != 0) throw CloseAndFail("grantpt");
        if (UnixNative.unlockpt(_master) != 0) throw CloseAndFail("unlockpt");
        var nameBuffer = new byte[256];
        if (UnixNative.ptsname_r(_master, nameBuffer, (nuint)nameBuffer.Length) != 0) throw CloseAndFail("ptsname_r");
        var slavePath = Encoding.UTF8.GetString(nameBuffer, 0, Array.IndexOf(nameBuffer, (byte)0));
        SetSize(Math.Max(cols, 20), Math.Max(rows, 10));

        var actions = Marshal.AllocHGlobal(UnixNative.OpaqueSize);
        var attr = Marshal.AllocHGlobal(UnixNative.OpaqueSize);
        var emptySet = Marshal.AllocHGlobal(UnixNative.OpaqueSize);
        var defaultSet = Marshal.AllocHGlobal(UnixNative.OpaqueSize);
        var strings = new List<IntPtr>();
        try
        {
            UnixNative.posix_spawn_file_actions_init(actions);
            UnixNative.posix_spawnattr_init(attr);

            // Standard input, output and error are the terminal; opened after setsid, it becomes the controlling terminal.
            Check(UnixNative.posix_spawn_file_actions_addopen(actions, 0, slavePath, UnixNative.O_RDWR, 0), "addopen");
            Check(UnixNative.posix_spawn_file_actions_adddup2(actions, 0, 1), "adddup2");
            Check(UnixNative.posix_spawn_file_actions_adddup2(actions, 0, 2), "adddup2");
            if (!string.IsNullOrWhiteSpace(workingDirectory))
                Check(UnixNative.posix_spawn_file_actions_addchdir_np(actions, workingDirectory), "addchdir");

            // A clean signal state: nothing blocked, every signal at its default (.NET ignores SIGPIPE, which would be inherited).
            UnixNative.sigemptyset(emptySet);
            UnixNative.sigfillset(defaultSet);
            Check(UnixNative.posix_spawnattr_setsigmask(attr, emptySet), "setsigmask");
            Check(UnixNative.posix_spawnattr_setsigdefault(attr, defaultSet), "setsigdefault");
            Check(UnixNative.posix_spawnattr_setflags(attr,
                (short)(UnixNative.POSIX_SPAWN_SETSID | UnixNative.POSIX_SPAWN_SETSIGMASK | UnixNative.POSIX_SPAWN_SETSIGDEF)), "setflags");

            var argv = CStrings(strings, [Shell, "-c", commandLine]);
            var envp = CStrings(strings, ChildEnvironment());
            var error = UnixNative.posix_spawn(out var pid, Shell, actions, attr, argv, envp);
            if (error != 0) throw new InvalidOperationException($"Cannot start '{commandLine}': posix_spawn failed ({UnixNative.ErrorText(error)}).");

            ProcessId = pid;
            _running = true;
        }
        catch
        {
            UnixNative.close(_master);
            _master = -1;
            throw;
        }
        finally
        {
            UnixNative.posix_spawn_file_actions_destroy(actions);
            UnixNative.posix_spawnattr_destroy(attr);
            Marshal.FreeHGlobal(actions);
            Marshal.FreeHGlobal(attr);
            Marshal.FreeHGlobal(emptySet);
            Marshal.FreeHGlobal(defaultSet);
            foreach (var s in strings) Marshal.FreeHGlobal(s);
        }

        new Thread(ReadLoop) { IsBackground = true, Name = "pty reader " + ProcessId }.Start();
        new Thread(WaitLoop) { IsBackground = true, Name = "pty waiter " + ProcessId }.Start();
    }

    /// <summary>The environment the CLI gets: this process's, with a terminal type a CLI can draw for.</summary>
    private static List<string> ChildEnvironment()
    {
        var variables = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (DictionaryEntry entry in System.Environment.GetEnvironmentVariables())
            variables[(string)entry.Key] = (string?)entry.Value ?? "";
        if (!variables.TryGetValue("TERM", out var term) || term is "" or "dumb") variables["TERM"] = "xterm-256color";
        if (!variables.ContainsKey("COLORTERM")) variables["COLORTERM"] = "truecolor";
        return variables.Select(v => v.Key + "=" + v.Value).ToList();
    }

    private static IntPtr[] CStrings(List<IntPtr> owned, IReadOnlyList<string> values)
    {
        var array = new IntPtr[values.Count + 1];
        for (var i = 0; i < values.Count; i++)
        {
            var bytes = Encoding.UTF8.GetBytes(values[i] + "\0");
            var p = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, p, bytes.Length);
            owned.Add(p);
            array[i] = p;
        }
        return array;
    }

    private void ReadLoop()
    {
        var buffer = new byte[4096];
        try
        {
            while (!_disposed)
            {
                var n = UnixNative.read(_master, buffer, buffer.Length);
                if (n < 0)
                {
                    if (Marshal.GetLastPInvokeError() is UnixNative.EINTR or UnixNative.EAGAIN) continue;
                    break; // EIO: every holder of the terminal's other side has closed it
                }
                if (n == 0) break;
                var copy = new byte[n];
                Buffer.BlockCopy(buffer, 0, copy, 0, (int)n);
                try { _onOutput(copy); } catch { }
            }
        }
        finally
        {
            _readerDone.Set();
        }
    }

    private void WaitLoop()
    {
        int exitCode;
        while (true)
        {
            if (UnixNative.waitpid(ProcessId, out var status, 0) == ProcessId)
            {
                var signal = status & 0x7f;
                exitCode = signal == 0 ? (status >> 8) & 0xff : 128 + signal;
                break;
            }
            if (Marshal.GetLastPInvokeError() == UnixNative.EINTR) continue;
            exitCode = -1; // reaped elsewhere (SIGCHLD ignored by whoever started this process)
            break;
        }

        // The last output comes before the exit, as the client expects. A background process that keeps the terminal
        // open must not hold the exit back for ever.
        _readerDone.Wait(OutputDrainWait);
        _running = false;
        try { _onExited(exitCode); } catch { }
    }

    public void Write(string data) => Write(Encoding.UTF8.GetBytes(data));

    public void Write(byte[] bytes)
    {
        if (_disposed || _master < 0 || bytes.Length == 0) return;
        lock (_writeLock)
        {
            var offset = 0;
            while (offset < bytes.Length)
            {
                var chunk = offset == 0 ? bytes : bytes[offset..];
                var n = UnixNative.write(_master, chunk, chunk.Length);
                if (n < 0)
                {
                    if (Marshal.GetLastPInvokeError() is UnixNative.EINTR or UnixNative.EAGAIN) continue;
                    return; // the terminal is gone
                }
                offset += (int)n;
            }
        }
    }

    public void Resize(int cols, int rows)
    {
        if (_disposed || _master < 0) return;
        SetSize(Math.Clamp(cols, 10, 500), Math.Clamp(rows, 5, 200));
    }

    private void SetSize(int cols, int rows)
    {
        var size = new UnixNative.WinSize { Cols = (ushort)cols, Rows = (ushort)rows };
        UnixNative.ioctl(_master, UnixNative.TIOCSWINSZ, ref size);
    }

    /// <summary>Ends the CLI and everything in its session (its process group), as closing a terminal window would.</summary>
    public void Kill()
    {
        if (!_running || ProcessId <= 0) return;
        if (UnixNative.kill(-ProcessId, UnixNative.SIGKILL) != 0) UnixNative.kill(ProcessId, UnixNative.SIGKILL);
    }

    public void Dispose()
    {
        if (_disposed) return;
        Kill();
        _disposed = true;
        // Close only once the reader has stopped, so its descriptor is never reused under it. A reader still blocked
        // by a leftover background process keeps the descriptor; it goes with this process.
        if (_readerDone.Wait(OutputDrainWait) && _master >= 0)
        {
            UnixNative.close(_master);
            _master = -1;
        }
    }

    private static void Check(int error, string what)
    {
        if (error != 0) throw new InvalidOperationException($"posix_spawn {what} failed ({UnixNative.ErrorText(error)}).");
    }

    private static InvalidOperationException Failure(string what) =>
        new($"{what} failed ({UnixNative.ErrorText(Marshal.GetLastPInvokeError())}).");

    private InvalidOperationException CloseAndFail(string what)
    {
        var failure = Failure(what);
        UnixNative.close(_master);
        _master = -1;
        return failure;
    }
}

/// <summary>Reads the program a shell command line runs, to report a missing one before starting it.</summary>
internal static class ShellCommand
{
    // Words /bin/sh runs itself; a command line starting with one is not checked.
    private static readonly HashSet<string> Builtins = new(StringComparer.Ordinal)
    {
        ".", ":", "cd", "command", "eval", "exec", "exit", "export", "set", "unset", "test", "[", "true", "false",
        "echo", "printf", "read", "trap", "ulimit", "umask", "wait", "if", "for", "while", "case", "{", "(", "!"
    };

    /// <summary>The first word that is not a <c>NAME=value</c> setting, without quotes; null for a shell builtin or keyword.</summary>
    public static string? FirstProgram(string commandLine)
    {
        foreach (var word in Words(commandLine))
        {
            if (IsAssignment(word)) continue;
            return Builtins.Contains(word) ? null : word;
        }
        return null;
    }

    /// <summary>The full path the shell would run for <paramref name="program"/>, or null when it is not there.</summary>
    public static string? Resolve(string program)
    {
        if (program.Contains('/')) return File.Exists(program) ? program : null;
        foreach (var dir in (System.Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir, program);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static bool IsAssignment(string word)
    {
        var eq = word.IndexOf('=');
        return eq > 0 && word[..eq].All(c => char.IsAsciiLetterOrDigit(c) || c == '_') && !char.IsAsciiDigit(word[0]);
    }

    // Splits on unquoted spaces, dropping single and double quotes; enough to find the first word.
    private static IEnumerable<string> Words(string commandLine)
    {
        var current = new StringBuilder();
        char? quote = null;
        var any = false;
        foreach (var c in commandLine)
        {
            if (quote is { } q)
            {
                if (c == q) quote = null;
                else current.Append(c);
            }
            else if (c is '\'' or '"') { quote = c; any = true; }
            else if (char.IsWhiteSpace(c))
            {
                if (current.Length > 0 || any) { yield return current.ToString(); current.Clear(); any = false; }
            }
            else current.Append(c);
        }
        if (current.Length > 0 || any) yield return current.ToString();
    }
}
