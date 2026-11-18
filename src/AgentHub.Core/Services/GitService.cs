using AgentHub.Models;

namespace AgentHub.Services;

public sealed class GitService(ProcessRunner runner)
{
    public async Task<bool> IsRepositoryAsync(string path)
    {
        var result = await runner.RunAsync("git", ["-C", path, "rev-parse", "--is-inside-work-tree"]);
        return result.ExitCode == 0 && result.StdOut.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<string?> GetRemoteUrlAsync(string path)
    {
        var result = await runner.RunAsync("git", ["-C", path, "remote", "get-url", "origin"]);
        return result.ExitCode == 0 ? result.StdOut.Trim() : null;
    }

    public async Task<GitSnapshot> GetSnapshotAsync(string path)
    {
        var branchResult = await runner.RunAsync("git", ["-C", path, "branch", "--show-current"]);
        var remoteResult = await runner.RunAsync("git", ["-C", path, "remote", "get-url", "origin"]);
        var statusResult = await runner.RunAsync("git", ["-C", path, "status", "--porcelain=v1", "--branch"]);

        var branch = string.IsNullOrWhiteSpace(branchResult.StdOut) ? "(detached)" : branchResult.StdOut.Trim();
        var remote = remoteResult.ExitCode == 0 ? remoteResult.StdOut.Trim() : "No origin";

        int ahead = 0, behind = 0, modified = 0, added = 0, deleted = 0, untracked = 0;
        var lines = statusResult.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var line in lines)
        {
            if (line.StartsWith("##"))
            {
                var aheadToken = "ahead ";
                var behindToken = "behind ";
                var aheadIndex = line.IndexOf(aheadToken, StringComparison.OrdinalIgnoreCase);
                var behindIndex = line.IndexOf(behindToken, StringComparison.OrdinalIgnoreCase);
                if (aheadIndex >= 0) ahead = ReadNumber(line, aheadIndex + aheadToken.Length);
                if (behindIndex >= 0) behind = ReadNumber(line, behindIndex + behindToken.Length);
                continue;
            }

            if (line.StartsWith("??")) { untracked++; continue; }
            if (line.Length < 2) continue;

            var x = line[0];
            var y = line[1];
            if (x == 'A' || y == 'A') added++;
            else if (x == 'D' || y == 'D') deleted++;
            else if (x == 'M' || y == 'M' || x == 'R' || y == 'R') modified++;
        }

        return new GitSnapshot(branch, remote, ahead, behind, modified, added, deleted, untracked, statusResult.StdOut);
    }

    public Task<(int ExitCode, string StdOut, string StdErr)> FetchAsync(string path) =>
        runner.RunAsync("git", ["-C", path, "fetch", "--prune"]);

    public Task<(int ExitCode, string StdOut, string StdErr)> PullAsync(string path) =>
        runner.RunAsync("git", ["-C", path, "pull", "--ff-only"]);

    public Task<(int ExitCode, string StdOut, string StdErr)> PushAsync(string path) =>
        runner.RunAsync("git", ["-C", path, "push"]);

    public Task<(int ExitCode, string StdOut, string StdErr)> CloneAsync(string url, string destination) =>
        runner.RunAsync("git", ["clone", url, destination]);

    private static int ReadNumber(string text, int start)
    {
        var digits = new string(text.Skip(start).TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out var value) ? value : 0;
    }
}
