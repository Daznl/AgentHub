using System.Net;
using System.Net.Http;
using System.Text.Json;
using AgentHub.Models;

namespace AgentHub.Services.Usage;

/// <summary>
/// Reads the user's real Claude Code subscription usage from Anthropic's OAuth usage endpoint
/// (<c>GET https://api.anthropic.com/api/oauth/usage</c>) — the same source the interactive
/// <c>/status</c> screen uses. The local OAuth access token is read from
/// <c>~/.claude/.credentials.json</c> (written by Claude Code's login). This is the clean,
/// non-scraping route to the live 5-hour / weekly limits.
/// </summary>
public static class ClaudeUsageReader
{
    private const string Endpoint = "https://api.anthropic.com/api/oauth/usage";
    private const string SourceLabel = "Anthropic usage API";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public static async Task<UsageSnapshot> ReadAsync(
        UsageProviderDefinition provider,
        string? credentialsPath = null,
        CancellationToken cancellationToken = default)
    {
        credentialsPath ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", ".credentials.json");

        if (!File.Exists(credentialsPath))
            return Fail(provider, UsageCollectionStatus.NotInstalled,
                "Claude Code login not found. Run `claude` and sign in.");

        string token;
        try
        {
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(credentialsPath, cancellationToken));
            if (!doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth) ||
                !oauth.TryGetProperty("accessToken", out var tokenProp) ||
                tokenProp.GetString() is not { Length: > 0 } value)
            {
                return Fail(provider, UsageCollectionStatus.NotInstalled, "No Claude OAuth token in credentials file.");
            }
            token = value;
        }
        catch (Exception ex)
        {
            return Fail(provider, UsageCollectionStatus.Failed, $"Could not read Claude credentials: {ex.Message}");
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint);
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
            request.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
            request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");

            using var response = await Http.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var hint = response.StatusCode == HttpStatusCode.Unauthorized
                    ? "Claude login expired — run `claude` to re-authenticate."
                    : $"Usage API returned {(int)response.StatusCode}.";
                return Fail(provider, UsageCollectionStatus.Failed, hint);
            }

            return ParseUsage(body, provider);
        }
        catch (OperationCanceledException)
        {
            return Fail(provider, UsageCollectionStatus.TimedOut, "Usage API request timed out.");
        }
        catch (Exception ex)
        {
            return Fail(provider, UsageCollectionStatus.Failed, ex.Message);
        }
    }

    /// <summary>Parses the /api/oauth/usage response body into a snapshot. Public for testing.</summary>
    public static UsageSnapshot ParseUsage(string json, UsageProviderDefinition provider)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { return Fail(provider, UsageCollectionStatus.UnsupportedOutput, $"Unexpected usage response: {ex.Message}"); }

        using (doc)
        {
            var root = doc.RootElement;
            var limits = new List<UsageLimit>();
            AddWindow(limits, root, "five_hour", "Session");
            AddWindow(limits, root, "seven_day", "Weekly");

            if (limits.Count == 0)
                return Fail(provider, UsageCollectionStatus.UnsupportedOutput, "No usage windows in API response.");

            var raw = string.Join(Environment.NewLine, limits.Select(l =>
                $"{l.Name}: {l.RemainingFraction * 100:0}% remaining" +
                (l.ResetsAt is { } r ? $" · resets {r.ToLocalTime():ddd h:mm tt}" : "")));

            return new UsageSnapshot(
                provider.Id, provider.Name, DateTimeOffset.Now, limits, SourceLabel,
                UsageCollectionStatus.Available, RawOutput: raw);
        }
    }

    private static void AddWindow(List<UsageLimit> limits, JsonElement root, string key, string name)
    {
        if (!root.TryGetProperty(key, out var window) || window.ValueKind != JsonValueKind.Object) return;
        if (!window.TryGetProperty("utilization", out var util) || !util.TryGetDouble(out var usedPercent)) return;

        DateTimeOffset? resetsAt = null;
        if (window.TryGetProperty("resets_at", out var resets) && resets.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(resets.GetString(), out var parsed))
        {
            resetsAt = parsed;
        }

        var remaining = Math.Clamp((100d - usedPercent) / 100d, 0d, 1d);
        limits.Add(new UsageLimit(name, remaining, resetsAt, null));
    }

    private static UsageSnapshot Fail(UsageProviderDefinition provider, UsageCollectionStatus status, string error) =>
        new(provider.Id, provider.Name, DateTimeOffset.Now, [], SourceLabel, status, error);
}
