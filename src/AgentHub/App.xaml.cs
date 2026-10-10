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

    // Markers a running Claude Code session sets for its own child processes. When AgentHub is started
    // from inside a Claude session they would reach every Cockpit pane, and a Claude launched there
    // would treat itself as a child session: no transcript (so no turn tracking) and no history.
    private static readonly string[] InheritedAgentSessionMarkers =
    [
        "CLAUDECODE", "CLAUDE_CODE_CHILD_SESSION", "CLAUDE_CODE_SESSION_ID", "CLAUDE_CODE_SESSION_ATTENDED",
        "CLAUDE_CODE_ENTRYPOINT", "CLAUDE_CODE_EXECPATH", "CLAUDE_CODE_MESSAGING_SOCKET", "CLAUDE_PID",
        "CLAUDE_EFFORT", "AI_AGENT"
    ];

    private static void ClearInheritedAgentSessionMarkers()
    {
        foreach (var name in InheritedAgentSessionMarkers)
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
