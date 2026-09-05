namespace AgentHub;

public partial class App : System.Windows.Application
{
    public App()
    {
        DispatcherUnhandledException += (s, e) => LogCrash("DispatcherUnhandledException", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (s, e) => LogCrash("AppDomainUnhandledException", e.ExceptionObject?.ToString());
        TaskScheduler.UnobservedTaskException += (s, e) => LogCrash("UnobservedTaskException", e.Exception);
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
