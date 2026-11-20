using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace AgentHub.Hosting;

/// <summary>
/// The C library calls behind <see cref="UnixPty"/>. Constants are Linux's generic values, the same on x86-64 and ARM64.
/// The opaque <c>posix_spawn</c> structures are allocated larger than glibc needs (80, 336 and 128 bytes).
/// </summary>
[SupportedOSPlatform("linux")]
internal static class UnixNative
{
    private const string Libc = "libc";

    public const int O_RDWR = 0x2;
    public const int O_NOCTTY = 0x100;
    public const int O_CLOEXEC = 0x80000;

    public const ulong TIOCSWINSZ = 0x5414;

    public const short POSIX_SPAWN_SETSIGDEF = 0x04;
    public const short POSIX_SPAWN_SETSIGMASK = 0x08;
    public const short POSIX_SPAWN_SETSID = 0x80;

    public const int SIGHUP = 1;
    public const int SIGKILL = 9;
    public const int SIGPIPE = 13;

    public const int EINTR = 4;
    public const int EAGAIN = 11;
    public const int ECHILD = 10;

    public const int OpaqueSize = 1024;

    [StructLayout(LayoutKind.Sequential)]
    public struct WinSize
    {
        public ushort Rows;
        public ushort Cols;
        public ushort XPixel;
        public ushort YPixel;
    }

    [DllImport(Libc, SetLastError = true)] public static extern int posix_openpt(int flags);
    [DllImport(Libc, SetLastError = true)] public static extern int grantpt(int fd);
    [DllImport(Libc, SetLastError = true)] public static extern int unlockpt(int fd);
    [DllImport(Libc, SetLastError = true)] public static extern int ptsname_r(int fd, byte[] buf, nuint buflen);
    [DllImport(Libc, SetLastError = true)] public static extern int ioctl(int fd, ulong request, ref WinSize size);
    [DllImport(Libc, SetLastError = true)] public static extern nint read(int fd, byte[] buf, nint count);
    [DllImport(Libc, SetLastError = true)] public static extern nint write(int fd, byte[] buf, nint count);
    [DllImport(Libc, SetLastError = true)] public static extern int close(int fd);
    [DllImport(Libc, SetLastError = true)] public static extern int kill(int pid, int sig);
    [DllImport(Libc, SetLastError = true)] public static extern int waitpid(int pid, out int status, int options);
    [DllImport(Libc, SetLastError = true)] public static extern int setsid();
    [DllImport(Libc, SetLastError = true)] public static extern uint geteuid();

    [DllImport(Libc)] public static extern int posix_spawn_file_actions_init(IntPtr actions);
    [DllImport(Libc)] public static extern int posix_spawn_file_actions_destroy(IntPtr actions);
    [DllImport(Libc)] public static extern int posix_spawn_file_actions_addopen(IntPtr actions, int fd, string path, int oflag, int mode);
    [DllImport(Libc)] public static extern int posix_spawn_file_actions_adddup2(IntPtr actions, int fd, int newFd);
    [DllImport(Libc)] public static extern int posix_spawn_file_actions_addchdir_np(IntPtr actions, string path);
    [DllImport(Libc)] public static extern int posix_spawnattr_init(IntPtr attr);
    [DllImport(Libc)] public static extern int posix_spawnattr_destroy(IntPtr attr);
    [DllImport(Libc)] public static extern int posix_spawnattr_setflags(IntPtr attr, short flags);
    [DllImport(Libc)] public static extern int posix_spawnattr_setsigmask(IntPtr attr, IntPtr sigset);
    [DllImport(Libc)] public static extern int posix_spawnattr_setsigdefault(IntPtr attr, IntPtr sigset);
    [DllImport(Libc)] public static extern int sigemptyset(IntPtr set);
    [DllImport(Libc)] public static extern int sigfillset(IntPtr set);

    /// <summary>Returns an error number (0 on success), not -1 and errno.</summary>
    [DllImport(Libc)] public static extern int posix_spawn(out int pid, string path, IntPtr fileActions, IntPtr attr, IntPtr[] argv, IntPtr[] envp);

    public static string ErrorText(int errno) => Marshal.GetPInvokeErrorMessage(errno);
}
