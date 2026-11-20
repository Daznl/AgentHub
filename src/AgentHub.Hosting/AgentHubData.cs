namespace AgentHub.Hosting;

/// <summary>
/// AgentHub's data folder: sessions, runs, history archive, orchestrator and helper working folders, host copies.
/// Windows: <c>C:\data\agenthub</c>, as always. Linux: the folder chosen at setup, passed as <c>AGENTHUB_DATA</c>
/// (docs/linux/PLAN.md), or <c>~/agenthub-data</c> when it is not set.
/// </summary>
public static class AgentHubData
{
    public const string WindowsRoot = @"C:\data\agenthub";
    public const string RootVariable = "AGENTHUB_DATA";

    public static string Root { get; } = Resolve();

    /// <summary>A folder inside the data folder, e.g. <c>Folder("runs")</c>.</summary>
    public static string Folder(params string[] parts) => Path.Combine([Root, .. parts]);

    private static string Resolve()
    {
        if (OperatingSystem.IsWindows()) return WindowsRoot;
        var chosen = Environment.GetEnvironmentVariable(RootVariable);
        return !string.IsNullOrWhiteSpace(chosen)
            ? Path.GetFullPath(chosen)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "agenthub-data");
    }
}
