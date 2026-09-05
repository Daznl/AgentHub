using System.Diagnostics;

namespace AgentHub.Services;

public sealed class RepoContextService
{
    public string EnsureHandoff(string repoPath)
    {
        var dir = Path.Combine(repoPath, ".agenthub");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "handoff.md");
        if (!File.Exists(path))
        {
            File.WriteAllText(path,
                "# Agent Handoff\n\n" +
                "## Current task\n\n- \n\n" +
                "## Completed\n\n- \n\n" +
                "## Next steps\n\n- \n\n" +
                "## Relevant files\n\n- \n");
        }
        return path;
    }

    public void OpenHandoff(string repoPath)
    {
        var path = EnsureHandoff(repoPath);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    public void OpenFolder(string repoPath) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{repoPath}\"") { UseShellExecute = true });

    public void OpenRemote(string remoteUrl)
    {
        if (string.IsNullOrWhiteSpace(remoteUrl)) return;
        var url = NormalizeRemoteToHttps(remoteUrl);
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    public static string NormalizeRemoteToHttps(string remote)
    {
        remote = remote.Trim();
        if (remote.StartsWith("git@github.com:", StringComparison.OrdinalIgnoreCase))
            remote = "https://github.com/" + remote["git@github.com:".Length..];
        if (remote.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            remote = remote[..^4];
        return remote;
    }
}
