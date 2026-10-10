namespace AgentHub.Tracking;

/// <summary>
/// Gives a Claude Code launch a session id chosen by AgentHub, so the pane knows exactly which
/// transcript file belongs to it instead of guessing from timestamps.
/// </summary>
public static class ClaudeSessionLaunch
{
    // Arguments that resume, fork or pin an existing session, or run non-interactively. Left untouched.
    private static readonly HashSet<string> SessionArguments = new(StringComparer.Ordinal)
    {
        "-r", "--resume", "-c", "--continue", "--session-id", "--fork-session", "-p", "--print"
    };

    /// <summary>
    /// Appends <c>--session-id &lt;new guid&gt;</c> when <paramref name="commandLine"/> starts Claude Code
    /// as a fresh interactive session. Returns false (and the command unchanged) for anything else.
    /// </summary>
    public static bool TryAssignSessionId(string commandLine, out string decorated, out string sessionId)
    {
        decorated = commandLine;
        sessionId = "";

        var (exe, rest) = SplitExecutable(commandLine.Trim());
        if (!string.Equals(Path.GetFileNameWithoutExtension(exe), "claude", StringComparison.OrdinalIgnoreCase))
            return false;

        var args = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (args.Any(a => SessionArguments.Contains(a.Split('=')[0])))
            return false;

        sessionId = Guid.NewGuid().ToString();
        decorated = $"{commandLine.TrimEnd()} --session-id {sessionId}";
        return true;
    }

    /// <summary>The executable part of a launch command line, without surrounding quotes.</summary>
    public static string ExecutableOf(string commandLine) => SplitExecutable(commandLine.Trim()).Exe;

    /// <summary>Claude Code's config folder: CLAUDE_CONFIG_DIR when set, otherwise ~/.claude.</summary>
    public static string ConfigDirectory =>
        Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } dir
            ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");

    /// <summary>Claude Code's per-folder transcript directory name: every character that is not a letter or digit becomes '-'.</summary>
    public static string EncodeProjectFolder(string workingDirectory) =>
        new(workingDirectory.Select(ch => char.IsAsciiLetterOrDigit(ch) ? ch : '-').ToArray());

    /// <summary>
    /// Finds the transcript for <paramref name="sessionId"/>, or null if Claude has not written it yet.
    /// <paramref name="searchAll"/> also searches every project folder, in case the naming scheme changes.
    /// </summary>
    public static string? FindTranscript(string sessionId, string workingDirectory, string? configDirectory = null, bool searchAll = true)
    {
        var projects = Path.Combine(configDirectory ?? ConfigDirectory, "projects");
        var fileName = sessionId + ".jsonl";

        var expected = Path.Combine(projects, EncodeProjectFolder(workingDirectory), fileName);
        if (File.Exists(expected)) return expected;

        if (!searchAll || !Directory.Exists(projects)) return null;
        try
        {
            return Directory.EnumerateFiles(projects, fileName, SearchOption.AllDirectories).FirstOrDefault();
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static (string Exe, string Arguments) SplitExecutable(string commandLine)
    {
        if (commandLine.StartsWith('"'))
        {
            var close = commandLine.IndexOf('"', 1);
            return close < 0
                ? (commandLine.Trim('"'), "")
                : (commandLine[1..close], commandLine[(close + 1)..]);
        }
        var space = commandLine.IndexOf(' ');
        return space < 0 ? (commandLine, "") : (commandLine[..space], commandLine[(space + 1)..]);
    }
}
