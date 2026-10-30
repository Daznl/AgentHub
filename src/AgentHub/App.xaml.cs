namespace AgentHub;

public partial class App : System.Windows.Application
{
    public App()
    {
        DispatcherUnhandledException += (s, e) => LogCrash("DispatcherUnhandledException", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (s, e) => LogCrash("AppDomainUnhandledException", e.ExceptionObject?.ToString());
        TaskScheduler.UnobservedTaskException += (s, e) => LogCrash("UnobservedTaskException", e.Exception);
        ClearInheritedAgentSessionMarkers();
    }

    /// <summary>
    /// When AgentHub is itself started from a Claude Code session, that session's markers are inherited and passed on to
    /// every CLI AgentHub launches. Claude Code then treats Cockpit sessions as its own children: it stops saving their
    /// transcripts (so no context badge) and hands them the parent's messaging token. Cockpit sessions are independent.
    /// Claude Code's tool shells also set <c>NO_COLOR</c> and <c>GIT_TERMINAL_PROMPT=0</c>; inherited, they strip colour from
    /// every pane and stop git asking for credentials. Those two are cleared only when a Claude Code parent is detected,
    /// so a user who sets them deliberately keeps them.
    /// </summary>
    private static void ClearInheritedAgentSessionMarkers()
    {
        if (Environment.GetEnvironmentVariable("CLAUDECODE") is not null)
        {
            Environment.SetEnvironmentVariable("NO_COLOR", null);
            Environment.SetEnvironmentVariable("GIT_TERMINAL_PROMPT", null);
        }

        string[] markers =
        [
            "CLAUDECODE", "CLAUDE_CODE_CHILD_SESSION", "CLAUDE_CODE_SESSION_ID", "CLAUDE_CODE_SESSION_ATTENDED",
            "CLAUDE_CODE_ENTRYPOINT", "CLAUDE_CODE_EXECPATH", "CLAUDE_CODE_MESSAGING_SOCKET", "CLAUDE_CODE_MESSAGING_TOKEN",
            "CLAUDE_PID", "CLAUDE_EFFORT"
        ];
        foreach (var name in markers)
            Environment.SetEnvironmentVariable(name, null);
    }

    private static void LogCrash(string category, object? exception)
    {
        try
        {
            var dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AgentHub");
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(dir, "crash.log"),
                $"[{category}] {DateTime.Now}: {exception}\n");
        }
        catch { }
    }
}
