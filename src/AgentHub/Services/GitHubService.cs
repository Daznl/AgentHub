using System.Text.Json;
using AgentHub.Models;

namespace AgentHub.Services;

public sealed class GitHubService
{
    private readonly ProcessRunner _runner;

    public GitHubService(ProcessRunner runner)
    {
        _runner = runner;
    }

    public async Task<bool> IsGhInstalledAsync()
    {
        try
        {
            var result = await _runner.RunAsync("gh", ["--version"]);
            return result.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public async Task<(bool IsAuthenticated, string? Username)> GetAuthUserAsync()
    {
        try
        {
            var result = await _runner.RunAsync("gh", ["api", "user", "--jq", ".login"]);
            if (result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.StdOut))
            {
                return (true, result.StdOut.Trim());
            }
        }
        catch
        {
        }

        return (false, null);
    }

    public async Task<List<GitHubRepository>> GetRepositoriesAsync(int limit = 100)
    {
        var result = await _runner.RunAsync("gh", [
            "repo", "list",
            "--json", "nameWithOwner,name,description,url,isPrivate,pushedAt,updatedAt",
            "--limit", limit.ToString()
        ]);

        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StdOut))
        {
            return [];
        }

        try
        {
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var list = JsonSerializer.Deserialize<List<GitHubRepository>>(result.StdOut, options);
            return list ?? [];
        }
        catch
        {
            return [];
        }
    }
}
