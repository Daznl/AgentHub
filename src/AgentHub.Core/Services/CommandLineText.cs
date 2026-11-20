namespace AgentHub.Services;

/// <summary>Reads a command line the same way on Windows and Linux.</summary>
public static class CommandLineText
{
    /// <summary>
    /// The program a command line starts, in lower case, without its folder or extension: <c>claude</c> for
    /// <c>"C:\Program Files\Claude\claude.exe" --resume</c> and for <c>/usr/local/bin/claude</c>. Both kinds of folder
    /// separator count on every system, so a Windows command line read on Linux gives the same answer.
    /// </summary>
    public static string ProgramName(string? commandLine)
    {
        var trimmed = (commandLine ?? "").TrimStart();
        string exe;
        if (trimmed.StartsWith('"'))
        {
            var close = trimmed.IndexOf('"', 1);
            exe = close > 0 ? trimmed[1..close] : trimmed[1..];
        }
        else
        {
            var space = trimmed.IndexOf(' ');
            exe = space > 0 ? trimmed[..space] : trimmed;
        }

        var name = exe[(exe.LastIndexOfAny(['\\', '/']) + 1)..];
        var dot = name.LastIndexOf('.');
        return (dot > 0 ? name[..dot] : name).ToLowerInvariant();
    }
}
