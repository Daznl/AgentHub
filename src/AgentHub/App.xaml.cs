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
    /// </summary>
    private static void ClearInheritedAgentSessionMarkers()
    {
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
