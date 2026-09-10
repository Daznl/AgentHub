using System.Text.Json;
using System.Text.RegularExpressions;
using AgentHub.Models;

namespace AgentHub.Services;

public sealed partial class GitHubService
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

    /// <summary>
    /// Lists every account the local GitHub CLI knows about across all hosts.
    /// Returns an empty list when gh is missing, has no accounts, or the output cannot be parsed.
    /// </summary>
    public async Task<List<GitHubAccount>> GetAccountsAsync()
    {
        try
        {
            // --json always exits 0 unless gh itself fails, even when a token is invalid.
            var result = await _runner.RunAsync("gh", ["auth", "status", "--json", "hosts"]);
            if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StdOut))
            {
                return [];
            }

            return ParseAccounts(result.StdOut);
        }
        catch
        {
            return [];
        }
    }

    /// <summary>Parses <c>gh auth status --json hosts</c> output: {"hosts":{"github.com":[{...}]}}.</summary>
    public static List<GitHubAccount> ParseAccounts(string json)
    {
        var accounts = new List<GitHubAccount>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("hosts", out var hosts) || hosts.ValueKind != JsonValueKind.Object)
            {
                return accounts;
            }

            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            foreach (var host in hosts.EnumerateObject())
            {
                if (host.Value.ValueKind != JsonValueKind.Array) continue;
                foreach (var entry in host.Value.EnumerateArray())
                {
                    var account = entry.Deserialize<GitHubAccount>(options);
                    if (account is null || string.IsNullOrWhiteSpace(account.Login)) continue;
                    if (string.IsNullOrWhiteSpace(account.Host)) account.Host = host.Name;
                    accounts.Add(account);
                }
            }
        }
        catch (JsonException)
        {
        }

        return accounts
            .OrderBy(a => a.Host, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(a => a.IsActive)
            .ThenBy(a => a.Login, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Makes <paramref name="login"/> the active gh account for <paramref name="host"/>.</summary>
    public async Task<(bool Succeeded, string? Error)> SwitchAccountAsync(string host, string login)
    {
        var result = await _runner.RunAsync("gh", ["auth", "switch", "--hostname", host, "--user", login]);
        return result.ExitCode == 0
            ? (true, null)
            : (false, FirstMeaningfulLine(result.StdErr) ?? "gh auth switch failed.");
    }

    /// <summary>Removes <paramref name="login"/> from the local gh credential store.</summary>
    public async Task<(bool Succeeded, string? Error)> LogoutAsync(string host, string login)
    {
        var result = await _runner.RunAsync("gh", ["auth", "logout", "--hostname", host, "--user", login]);
        return result.ExitCode == 0
            ? (true, null)
            : (false, FirstMeaningfulLine(result.StdErr) ?? "gh auth logout failed.");
    }

    /// <summary>
    /// Runs the real GitHub device-flow sign-in (<c>gh auth login --web</c>) without a terminal.
    /// gh prints a one-time code and verification URL, which are surfaced through <paramref name="onProgress"/>;
    /// the user approves in their browser and gh stores the resulting token itself. AgentHub never sees it.
    /// The new account becomes gh's active account for that host.
    /// </summary>
    public async Task<GitHubLoginResult> LoginWithBrowserAsync(
        string host,
        Action<GitHubLoginProgress> onProgress,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        host = string.IsNullOrWhiteSpace(host) ? "github.com" : host.Trim();
        string? code = null;
        string? url = null;
        string? login = null;

        void HandleLine(string line)
        {
            var codeMatch = OneTimeCodeRegex().Match(line);
            if (codeMatch.Success) code = codeMatch.Groups["code"].Value;

            var urlMatch = VerificationUrlRegex().Match(line);
            if (urlMatch.Success) url = urlMatch.Groups["url"].Value;

            var loginMatch = LoggedInAsRegex().Match(line);
            if (loginMatch.Success) login = loginMatch.Groups["login"].Value;

            onProgress(new GitHubLoginProgress(code, url, line));
        }

        try
        {
            // Non-interactive gh (stdin closed) skips every prompt, prints the device code + URL to stderr,
            // and polls GitHub until the user approves or the code expires (~15 minutes).
            var result = await _runner.RunStreamingAsync(
                "gh",
                [
                    "auth", "login",
                    "--hostname", host,
                    "--git-protocol", "https",
                    "--web",
                    "--skip-ssh-key"
                ],
                HandleLine,
                cancellationToken: cancellationToken,
                timeout: timeout ?? TimeSpan.FromMinutes(16));

            if (result.ExitCode == 0)
            {
                return new GitHubLoginResult(true, login, null);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return new GitHubLoginResult(false, null, "Sign-in cancelled.");
            }

            return new GitHubLoginResult(false, null, FirstMeaningfulLine(result.StdErr) ?? "gh auth login failed.");
        }
        catch (Exception ex)
        {
            return new GitHubLoginResult(false, null, ex.Message);
        }
    }

    /// <summary>
    /// One paginated GraphQL query covering every repository the signed-in user can see: their own,
    /// ones shared with them, and every organisation they belong to. gh emits one JSON object per repo
    /// as each page arrives, so <paramref name="onRepository"/> fires progressively (on a worker thread).
    /// Returns the total count, or an error message when gh failed before producing anything.
    /// </summary>
    public async Task<(int Count, string? Error)> StreamAllRepositoriesAsync(
        string viewerLogin,
        Action<GitHubRepository> onRepository,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        var count = 0;
        var noise = new List<string>();

        void HandleLine(string line)
        {
            var repo = ParseRepositoryLine(line, viewerLogin);
            if (repo is null)
            {
                if (!string.IsNullOrWhiteSpace(line)) noise.Add(line);
                return;
            }

            Interlocked.Increment(ref count);
            onRepository(repo);
        }

        var result = await _runner.RunStreamingAsync(
            "gh",
            [
                "api", "graphql",
                "--paginate",
                "-f", $"query={AllRepositoriesQuery}",
                "--jq", ".data.viewer.repositories.nodes[]"
            ],
            HandleLine,
            cancellationToken: cancellationToken,
            timeout: timeout ?? TimeSpan.FromMinutes(3));

        if (result.ExitCode != 0 && count == 0)
        {
            var error = FirstMeaningfulLine(result.StdErr) ?? noise.FirstOrDefault() ?? "gh api graphql failed.";
            return (0, error);
        }

        return (count, null);
    }

    /// <summary>Parses one NDJSON line from the repositories query. Returns null for non-JSON noise.</summary>
    public static GitHubRepository? ParseRepositoryLine(string line, string viewerLogin)
    {
        line = line.Trim();
        if (line.Length == 0 || line[0] != '{') return null;

        try
        {
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var repo = JsonSerializer.Deserialize<GitHubRepository>(line, options);
            if (repo is null || string.IsNullOrWhiteSpace(repo.NameWithOwner)) return null;

            repo.IsOwnedByViewer = string.Equals(repo.OwnerLogin, viewerLogin, StringComparison.OrdinalIgnoreCase);
            return repo;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private const string AllRepositoriesQuery =
        "query($endCursor: String) { viewer { repositories(first: 100, after: $endCursor, " +
        "affiliations: [OWNER, COLLABORATOR, ORGANIZATION_MEMBER], ownerAffiliations: [OWNER, COLLABORATOR, ORGANIZATION_MEMBER], " +
        "orderBy: {field: PUSHED_AT, direction: DESC}) { pageInfo { hasNextPage endCursor } " +
        "nodes { name nameWithOwner description url isPrivate isArchived isFork isInOrganization pushedAt updatedAt viewerPermission owner { login __typename } } } } }";

    /// <summary>First non-empty line of gh's stderr, with its leading status glyph (!, check mark, X, -) removed.</summary>
    public static string? FirstMeaningfulLine(string text)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().TrimStart('!', '✓', 'X', '-').Trim();
            if (line.Length > 0) return line;
        }
        return null;
    }

    // "! First copy your one-time code: 7A95-6FD0"
    [GeneratedRegex(@"one-time code:\s*(?<code>[A-Z0-9]{4}-[A-Z0-9]{4})", RegexOptions.IgnoreCase)]
    private static partial Regex OneTimeCodeRegex();

    // "Open this URL to continue in your web browser: https://github.com/login/device"
    [GeneratedRegex(@"(?<url>https?://\S+/login/device\S*)", RegexOptions.IgnoreCase)]
    private static partial Regex VerificationUrlRegex();

    // "(check) Logged in as DanielWallisBarker"
    [GeneratedRegex(@"Logged in as\s+(?<login>[A-Za-z0-9][A-Za-z0-9-]*)", RegexOptions.IgnoreCase)]
    private static partial Regex LoggedInAsRegex();
}
