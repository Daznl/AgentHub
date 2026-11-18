using AgentHub.Models;

namespace AgentHub.Services;

public sealed record DiscoveredRepository(
    string Name,
    string LocalPath,
    string? RemoteUrl,
    GitSnapshot? Snapshot
);

public sealed class RepoDiscoveryService
{
    private readonly GitService _git;

    public RepoDiscoveryService(GitService git)
    {
        _git = git;
    }

    public async Task<List<DiscoveredRepository>> ScanFolderAsync(string rootPath, int maxDepth = 2)
    {
        var results = new List<DiscoveredRepository>();
        if (!Directory.Exists(rootPath)) return results;

        var foundDirs = new List<string>();
        await Task.Run(() =>
        {
            FindGitDirectories(new DirectoryInfo(rootPath), 0, maxDepth, foundDirs);
        });

        foreach (var dir in foundDirs)
        {
            try
            {
                var isRepo = await _git.IsRepositoryAsync(dir);
                if (!isRepo) continue;

                var remoteUrl = await _git.GetRemoteUrlAsync(dir);
                var snapshot = await _git.GetSnapshotAsync(dir);
                var name = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

                results.Add(new DiscoveredRepository(name, dir, remoteUrl, snapshot));
            }
            catch
            {
                // Skip unreadable or corrupted repositories
            }
        }

        return results;
    }

    private static void FindGitDirectories(DirectoryInfo dir, int currentDepth, int maxDepth, List<string> results)
    {
        if (currentDepth > maxDepth) return;

        try
        {
            var gitPath = Path.Combine(dir.FullName, ".git");
            if (Directory.Exists(gitPath) || File.Exists(gitPath))
            {
                results.Add(dir.FullName);
                return; // Do not recurse inside a git repo
            }

            foreach (var sub in dir.EnumerateDirectories())
            {
                if (sub.Attributes.HasFlag(FileAttributes.Hidden) ||
                    sub.Attributes.HasFlag(FileAttributes.System) ||
                    sub.Name.Equals("node_modules", StringComparison.OrdinalIgnoreCase) ||
                    sub.Name.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                    sub.Name.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
                    sub.Name.Equals(".vs", StringComparison.OrdinalIgnoreCase) ||
                    sub.Name.Equals(".git", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                FindGitDirectories(sub, currentDepth + 1, maxDepth, results);
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (DirectoryNotFoundException) { }
        catch (PathTooLongException) { }
    }

    public static bool MatchesUrl(string? urlA, string? urlB)
    {
        if (string.IsNullOrWhiteSpace(urlA) || string.IsNullOrWhiteSpace(urlB)) return false;
        var normA = NormalizeGitUrl(urlA);
        var normB = NormalizeGitUrl(urlB);
        return string.Equals(normA, normB, StringComparison.OrdinalIgnoreCase);
    }

    public static string NormalizeGitUrl(string url)
    {
        var trimmed = url.Trim().TrimEnd('/');
        if (trimmed.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[..^4];

        // Convert git@github.com:User/Repo to https://github.com/User/Repo
        if (trimmed.StartsWith("git@github.com:", StringComparison.OrdinalIgnoreCase))
            trimmed = "https://github.com/" + trimmed["git@github.com:".Length..];

        return trimmed.ToLowerInvariant();
    }
}
