using System;
using System.IO;
using Xunit;

namespace AgentHub.Tests;

/// <summary>Folder paths for tests that run on Windows and on Linux.</summary>
public static class TestPaths
{
    /// <summary>
    /// A Windows path as written in a test, on Windows unchanged; on Linux the same folder as a Linux path, its drive kept
    /// as the first folder so two drives stay apart: <c>C:\Work\My Repo</c> → <c>/c/Work/My Repo</c>.
    /// </summary>
    public static string Native(string windowsPath)
    {
        if (OperatingSystem.IsWindows()) return windowsPath;
        var path = windowsPath.Length >= 2 && windowsPath[1] == ':'
            ? "/" + char.ToLowerInvariant(windowsPath[0]) + windowsPath[2..]
            : windowsPath;
        return path.Replace('\\', '/');
    }

    /// <summary>A relative path written with backslashes, with this system's separator.</summary>
    public static string Relative(string windowsRelative) => windowsRelative.Replace('\\', Path.DirectorySeparatorChar);
}

/// <summary>A fact about Windows behaviour that Linux does not have; reported as skipped elsewhere, with the reason.</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute(string whyWindowsOnly)
    {
        if (!OperatingSystem.IsWindows()) Skip = "Windows only: " + whyWindowsOnly;
    }
}
