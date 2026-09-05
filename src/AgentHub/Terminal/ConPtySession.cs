using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AgentHub.Terminal;

public sealed class ConPtySession : IDisposable
{
    private IntPtr _hPC = IntPtr.Zero;
    private IntPtr _hProcess = IntPtr.Zero;
    private IntPtr _hThread = IntPtr.Zero;
    private IntPtr _hPipeInWrite = IntPtr.Zero;
    private FileStream? _outStream;
    private CancellationTokenSource? _readCts;
    private bool _disposed;

    public int ProcessId { get; private set; }
    public bool IsRunning { get; private set; }

    public event Action<string>? OutputDataReceived;
    public event Action<int>? ProcessExited;

    public static ConPtySession Start(string commandLine, string workingDirectory, int cols = 120, int rows = 30)
    {
        var session = new ConPtySession();
        session.Initialize(commandLine, workingDirectory, cols, rows);
        return session;
    }

    private void Initialize(string commandLine, string workingDirectory, int cols, int rows)
    {
        var sa = new ConPtyNative.SECURITY_ATTRIBUTES
        {
            nLength = Marshal.SizeOf<ConPtyNative.SECURITY_ATTRIBUTES>(),
            bInheritHandle = true
        };

        if (!ConPtyNative.CreatePipe(out var hPipeInRead, out var hPipeInWrite, ref sa, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to create input pipe");

        if (!ConPtyNative.CreatePipe(out var hPipeOutRead, out var hPipeOutWrite, ref sa, 0))
        {
            ConPtyNative.CloseHandle(hPipeInRead);
            ConPtyNative.CloseHandle(hPipeInWrite);
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to create output pipe");
        }

        ConPtyNative.SetHandleInformation(hPipeInWrite, ConPtyNative.HANDLE_FLAG_INHERIT, 0);
        ConPtyNative.SetHandleInformation(hPipeOutRead, ConPtyNative.HANDLE_FLAG_INHERIT, 0);
        _hPipeInWrite = hPipeInWrite;

        var coord = new ConPtyNative.COORD
        {
            X = (short)Math.Max(cols, 20),
            Y = (short)Math.Max(rows, 10)
        };

        var hr = ConPtyNative.CreatePseudoConsole(coord, hPipeInRead, hPipeOutWrite, 0, out _hPC);

        // PseudoConsole takes ownership of read-in and write-out handles
        ConPtyNative.CloseHandle(hPipeInRead);
        ConPtyNative.CloseHandle(hPipeOutWrite);

        if (hr != 0)
        {
            ConPtyNative.CloseHandle(hPipeInWrite);
            ConPtyNative.CloseHandle(hPipeOutRead);
            throw new Win32Exception(hr, $"CreatePseudoConsole failed with HRESULT 0x{hr:X8}");
        }

        IntPtr lpAttributeList = IntPtr.Zero;
        try
        {
            var lpSize = IntPtr.Zero;
            ConPtyNative.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref lpSize);
            lpAttributeList = Marshal.AllocHGlobal(lpSize);
            if (!ConPtyNative.InitializeProcThreadAttributeList(lpAttributeList, 1, 0, ref lpSize))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "InitializeProcThreadAttributeList failed");

            if (!ConPtyNative.UpdateProcThreadAttribute(
                lpAttributeList,
                0,
                ConPtyNative.PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                _hPC,
                (IntPtr)IntPtr.Size,
                IntPtr.Zero,
                IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "UpdateProcThreadAttribute failed");
            }

            var startupInfo = new ConPtyNative.STARTUPINFOEX();
            startupInfo.StartupInfo.cb = Marshal.SizeOf<ConPtyNative.STARTUPINFOEX>();
            startupInfo.lpAttributeList = lpAttributeList;

            var processSa = new ConPtyNative.SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<ConPtyNative.SECURITY_ATTRIBUTES>() };
            var threadSa = new ConPtyNative.SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<ConPtyNative.SECURITY_ATTRIBUTES>() };

            if (!ConPtyNative.CreateProcess(
                null,
                commandLine,
                ref processSa,
                ref threadSa,
                false,
                ConPtyNative.EXTENDED_STARTUPINFO_PRESENT,
                IntPtr.Zero,
                string.IsNullOrWhiteSpace(workingDirectory) ? null : workingDirectory,
                ref startupInfo,
                out var processInfo))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"CreateProcess failed for command '{commandLine}'");
            }

            _hProcess = processInfo.hProcess;
            _hThread = processInfo.hThread;
            ProcessId = processInfo.dwProcessId;
            IsRunning = true;
        }
        finally
        {
            if (lpAttributeList != IntPtr.Zero)
            {
                ConPtyNative.DeleteProcThreadAttributeList(lpAttributeList);
                Marshal.FreeHGlobal(lpAttributeList);
            }
        }

        var safeOutHandle = new SafeFileHandle(hPipeOutRead, ownsHandle: true);
        _outStream = new FileStream(safeOutHandle, FileAccess.Read, 4096, isAsync: false);

        _readCts = new CancellationTokenSource();
        Task.Run(ReadOutputLoopAsync);
        Task.Run(MonitorProcessExitAsync);
    }

    public void Write(string data)
    {
        if (_disposed || _hPipeInWrite == IntPtr.Zero) return;
        try
        {
            var bytes = Encoding.UTF8.GetBytes(data);
            ConPtyNative.WriteFile(_hPipeInWrite, bytes, (uint)bytes.Length, out _, IntPtr.Zero);
        }
        catch { }
    }

    public void Resize(int cols, int rows)
    {
        if (_disposed || _hPC == IntPtr.Zero) return;
        var coord = new ConPtyNative.COORD
        {
            X = (short)Math.Clamp(cols, 10, 500),
            Y = (short)Math.Clamp(rows, 5, 200)
        };
        ConPtyNative.ResizePseudoConsole(_hPC, coord);
    }

    private void ReadOutputLoopAsync()
    {
        var buffer = new byte[4096];
        var decoder = Encoding.UTF8.GetDecoder();
        var charBuffer = new char[4096];
        try
        {
            while (!_disposed && _outStream is not null)
            {
                var bytesRead = _outStream.Read(buffer, 0, buffer.Length);
                if (bytesRead <= 0) break;

                var charCount = decoder.GetChars(buffer, 0, bytesRead, charBuffer, 0, flush: false);
                if (charCount > 0)
                {
                    var text = new string(charBuffer, 0, charCount);
                    OutputDataReceived?.Invoke(text);
                }
            }
        }
        catch { }
    }

    private void MonitorProcessExitAsync()
    {
        if (_hProcess == IntPtr.Zero) return;
        try
        {
            using var waitHandle = new SafeWaitHandle(_hProcess, ownsHandle: false);
            using var mre = new ManualResetEvent(false) { SafeWaitHandle = waitHandle };
            mre.WaitOne();

            int exitCode = 0;
            if (GetExitCodeProcess(_hProcess, out var code))
                exitCode = code;

            IsRunning = false;
            ProcessExited?.Invoke(exitCode);
        }
        catch { }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(IntPtr hProcess, out int lpExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

    public void Kill()
    {
        if (_hProcess != IntPtr.Zero && IsRunning)
        {
            TerminateProcess(_hProcess, 1);
            IsRunning = false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Kill();

        _readCts?.Cancel();

        if (_hPipeInWrite != IntPtr.Zero)
        {
            ConPtyNative.CloseHandle(_hPipeInWrite);
            _hPipeInWrite = IntPtr.Zero;
        }

        try { _outStream?.Dispose(); } catch { }

        if (_hPC != IntPtr.Zero)
        {
            ConPtyNative.ClosePseudoConsole(_hPC);
            _hPC = IntPtr.Zero;
        }

        if (_hThread != IntPtr.Zero)
        {
            ConPtyNative.CloseHandle(_hThread);
            _hThread = IntPtr.Zero;
        }

        if (_hProcess != IntPtr.Zero)
        {
            ConPtyNative.CloseHandle(_hProcess);
            _hProcess = IntPtr.Zero;
        }
    }
}
