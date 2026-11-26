using System.Text.Json;
using AgentHub.Models;

namespace AgentHub.Services.Move;

/// <summary>
/// Settings from a move package. The running AgentHub saves its settings on exit, so an import never writes
/// settings.json itself: it leaves <see cref="PendingFileName"/> and the next start merges it here, before anything reads
/// the settings.
/// </summary>
public static class SettingsImport
{
    public const string PendingFileName = "settings.import-pending.json";

    /// <summary>
    /// The package's settings, with this computer's added: its repositories and panes stay, alongside the imported
    /// ones. Folders that belong to this computer (history archive, session hosts) stay as they are here. Imported panes
    /// come back without a running session: those sessions ran on the other computer; each keeps its transcript to resume.
    /// </summary>
    public static AppSettings Merge(AppSettings current, AppSettings imported)
    {
        var merged = imported;
        merged.HistoryArchiveDirectory = current.HistoryArchiveDirectory;
        merged.SessionsDirectory = current.SessionsDirectory;

        var repos = new List<RepositoryDefinition>(imported.Repositories);
        foreach (var repo in current.Repositories)
        {
            if (!repos.Any(r => SamePath(r.LocalPath, repo.LocalPath))) repos.Add(repo);
        }
        merged.Repositories = repos;

        var ssh = new List<SshConnection>(imported.SshConnections);
        foreach (var connection in current.SshConnections)
        {
            if (!ssh.Any(s => s.Id == connection.Id || string.Equals(s.Target, connection.Target, StringComparison.OrdinalIgnoreCase)))
                ssh.Add(connection);
        }
        merged.SshConnections = ssh;

        var panes = new List<PaneLayoutEntry>(current.PaneLayout);
        foreach (var pane in imported.PaneLayout)
        {
            pane.SessionId = null;
            if (panes.Any(p => SamePath(p.Folder, pane.Folder) && SamePath(p.TranscriptPath, pane.TranscriptPath))) continue;
            panes.Add(pane);
        }
        merged.PaneLayout = panes;
        return merged;
    }

    private static bool SamePath(string? a, string? b) =>
        string.Equals(a?.TrimEnd('\\', '/'), b?.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Merges a pending import into <paramref name="current"/> when one is waiting in <paramref name="settingsDir"/>.
    /// The settings before the merge are kept as <c>settings.before-import-&lt;time&gt;.json</c> and the pending file is
    /// renamed to <c>settings.import-applied-&lt;time&gt;.json</c>, so it is merged once. Returns null when none is waiting.
    /// </summary>
    public static AppSettings? ApplyPending(AppSettings current, string settingsDir, string settingsPath, JsonSerializerOptions json)
    {
        var pending = Path.Combine(settingsDir, PendingFileName);
        if (!File.Exists(pending)) return null;

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var imported = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(pending), json)
            ?? throw new InvalidDataException("The imported settings are empty.");
        if (File.Exists(settingsPath)) File.Copy(settingsPath, Path.Combine(settingsDir, $"settings.before-import-{stamp}.json"), overwrite: true);
        var merged = Merge(current, imported);
        File.Move(pending, Path.Combine(settingsDir, $"settings.import-applied-{stamp}.json"), overwrite: true);
        return merged;
    }
}
