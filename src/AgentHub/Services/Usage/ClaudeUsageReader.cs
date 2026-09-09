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
///
/// The endpoint is IP-rate-limited (returns HTTP 429 when polled too often), so this reader is
/// deliberately conservative: it caches the last good reading, refuses to call the network more
/// than once per <see cref="MinNetworkInterval"/> (serving the cache in between), and — when a
/// call does fail transiently (429 / network blip) — keeps returning the last good numbers rather
/// than blanking the card. Only genuinely actionable states (login missing / expired) surface as
/// failures.
/// </summary>
public static class ClaudeUsageReader
{
    private const string Endpoint = "https://api.anthropic.com/api/oauth/usage";
    private const string SourceLabel = "Anthropic usage API";

    /// <summary>Minimum gap between real network calls. The 5-hour / weekly windows move slowly,
    /// so polling more often than this only risks a 429. Extra UI refreshes are served from cache.</summary>
    private static readonly TimeSpan MinNetworkInterval = TimeSpan.FromSeconds(60);

    /// <summary>Default back-off applied after a 429 when the server sends no <c>Retry-After</c>.</summary>
    private static readonly TimeSpan RateLimitBackoff = TimeSpan.FromSeconds(90);

    /// <summary>Cached numbers older than this are shown as <see cref="UsageCollectionStatus.Stale"/>
    /// while a refresh keeps failing, so a long outage is visible rather than silently frozen.</summary>
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(15);

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    // Guards the cache + throttle fields. Held only around synchronous reads/writes, never across an await.
    private static readonly object Gate = new();
    private static UsageSnapshot? _cached;
    private static DateTimeOffset _nextNetworkCall = DateTimeOffset.MinValue;

    public static async Task<UsageSnapshot> ReadAsync(
        UsageProviderDefinition provider,
        string? credentialsPath = null,
        CancellationToken cancellationToken = default)
    {
        // If we have a recent good reading, serve it without touching the network. This keeps the
        // card populated and, more importantly, stops aggressive UI polling from tripping the 429.
        lock (Gate)
        {
            if (_cached is not null && DateTimeOffset.Now < _nextNetworkCall)
                return ServeCache(provider);
        }

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

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                // Rate-limited. Back off (honouring Retry-After) and keep showing the last good numbers.
                var backoff = response.Headers.RetryAfter?.Delta
                    ?? (response.Headers.RetryAfter?.Date - DateTimeOffset.Now)
                    ?? RateLimitBackoff;
                Throttle(backoff);
                return SoftFail(provider, "Rate-limited by the usage API — showing last reading.");
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                // Actionable: the login is gone/expired. Surface it (don't mask with cache).
                Throttle(MinNetworkInterval);
                return Fail(provider, UsageCollectionStatus.Failed,
                    "Claude login expired — run `claude` to re-authenticate.");
            }

            if (!response.IsSuccessStatusCode)
            {
                Throttle(RateLimitBackoff);
                return SoftFail(provider, $"Usage API returned {(int)response.StatusCode} — showing last reading.");
            }

            var snapshot = ParseUsage(body, provider);
            if (snapshot.Status == UsageCollectionStatus.Available)
            {
                lock (Gate)
                {
                    _cached = snapshot;
                    _nextNetworkCall = DateTimeOffset.Now + MinNetworkInterval;
                }
                return snapshot;
            }

            // Parsed but unusable (shape changed): keep the last good numbers if we have them.
            return SoftFail(provider, snapshot.Error ?? "Unexpected usage response.");
        }
        catch (OperationCanceledException)
        {
            return SoftFail(provider, "Usage API request timed out — showing last reading.", UsageCollectionStatus.TimedOut);
        }
        catch (Exception ex)
        {
            Throttle(MinNetworkInterval);
            return SoftFail(provider, $"{ex.Message} — showing last reading.");
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

    private static void Throttle(TimeSpan backoff)
    {
        // Clamp so a bogus Retry-After can't freeze us out for hours or spin us in a tight loop.
        var delay = backoff < MinNetworkInterval ? MinNetworkInterval
            : backoff > TimeSpan.FromMinutes(30) ? TimeSpan.FromMinutes(30)
            : backoff;
        lock (Gate) { _nextNetworkCall = DateTimeOffset.Now + delay; }
    }

    /// <summary>Returns the cached good reading (kept fresh in the UI). Used on the throttle fast-path.</summary>
    private static UsageSnapshot ServeCache(UsageProviderDefinition provider)
    {
        var c = _cached!;
        return c with { ProviderId = provider.Id, ProviderName = provider.Name };
    }

    /// <summary>
    /// A refresh failed transiently. If we still have a good reading, keep showing its numbers —
    /// marked <see cref="UsageCollectionStatus.Available"/> while recent, or <c>Stale</c> once it
    /// ages past <see cref="StaleAfter"/> so a prolonged outage becomes visible. With no cache at
    /// all, surface the underlying failure.
    /// </summary>
    private static UsageSnapshot SoftFail(
        UsageProviderDefinition provider, string reason, UsageCollectionStatus hardStatus = UsageCollectionStatus.Failed)
    {
        UsageSnapshot? cached;
        lock (Gate) { cached = _cached; }

        if (cached is null)
            return Fail(provider, hardStatus, reason);

        var aged = DateTimeOffset.Now - cached.CapturedAt > StaleAfter;
        return cached with
        {
            ProviderId = provider.Id,
            ProviderName = provider.Name,
            Status = aged ? UsageCollectionStatus.Stale : UsageCollectionStatus.Available,
            Error = aged ? reason : null,
        };
    }

    private static UsageSnapshot Fail(UsageProviderDefinition provider, UsageCollectionStatus status, string error) =>
        new(provider.Id, provider.Name, DateTimeOffset.Now, [], SourceLabel, status, error);
}
