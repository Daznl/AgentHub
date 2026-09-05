using System.Text.Json;
using AgentHub.Models;

namespace AgentHub.Services.Usage;

/// <summary>
/// Reads Codex usage limits directly from its on-disk session rollout files.
/// Codex records the latest server-reported rate limits (as an API response side effect)
/// into <c>~/.codex/sessions/&lt;yyyy&gt;/&lt;mm&gt;/&lt;dd&gt;/rollout-*.jsonl</c> as a
/// <c>rate_limits</c> object with <c>primary</c> (5-hour) and <c>secondary</c> (weekly) windows.
/// This is far more robust than scraping the interactive TUI.
/// </summary>
public static class CodexUsageReader
{
    private const string Source = "~/.codex session rate_limits";

    public static UsageSnapshot Read(UsageProviderDefinition provider, string? sessionsDir = null)
    {
        sessionsDir ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "sessions");

        if (!Directory.Exists(sessionsDir))
            return Fail(provider, UsageCollectionStatus.NotInstalled, "No Codex session data found on disk.");

        FileInfo? newest;
        try
        {
            newest = new DirectoryInfo(sessionsDir)
                .EnumerateFiles("rollout-*.jsonl", SearchOption.AllDirectories)
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch (Exception ex)
        {
            return Fail(provider, UsageCollectionStatus.Failed, ex.Message);
        }

        if (newest is null)
            return Fail(provider, UsageCollectionStatus.NotInstalled, "No Codex session files found.");

        JsonElement? lastRateLimits = null;
        try
        {
            foreach (var line in File.ReadLines(newest.FullName))
            {
                if (!line.Contains("\"rate_limits\"", StringComparison.Ordinal)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    if (TryFindRateLimits(doc.RootElement, out var element) &&
                        element.TryGetProperty("primary", out _))
                    {
                        lastRateLimits = element.Clone();
                    }
                }
                catch (JsonException) { /* skip malformed lines */ }
            }
        }
        catch (Exception ex)
        {
            return Fail(provider, UsageCollectionStatus.Failed, ex.Message);
        }

        if (lastRateLimits is null)
            return Fail(provider, UsageCollectionStatus.UnsupportedOutput,
                "No rate-limit data recorded in latest Codex session yet. Run Codex once to populate.");

        var limits = new List<UsageLimit>();
        AddLimit(limits, lastRateLimits.Value, "primary");
        AddLimit(limits, lastRateLimits.Value, "secondary");

        if (limits.Count == 0)
            return Fail(provider, UsageCollectionStatus.UnsupportedOutput, "Codex rate-limit block had no windows.");

        return new UsageSnapshot(
            provider.Id,
            provider.Name,
            newest.LastWriteTime,
            limits,
            Source,
            UsageCollectionStatus.Available,
            RawOutput: lastRateLimits.Value.GetRawText());
    }

    private static void AddLimit(List<UsageLimit> limits, JsonElement rateLimits, string key)
    {
        if (!rateLimits.TryGetProperty(key, out var window) || window.ValueKind != JsonValueKind.Object)
            return;
        if (!window.TryGetProperty("used_percent", out var usedProp) || !usedProp.TryGetDouble(out var used))
            return;

        var windowMinutes = window.TryGetProperty("window_minutes", out var wm) && wm.TryGetInt32(out var minutes)
            ? minutes
            : 0;

        var name = windowMinutes switch
        {
            > 0 and <= 360 => "5-Hour",
            >= 1440 => "Weekly",
            _ => key == "primary" ? "5-Hour" : "Weekly"
        };

        DateTimeOffset? resetsAt = null;
        if (window.TryGetProperty("resets_at", out var resets) && resets.TryGetInt64(out var unixSeconds))
        {
            try { resetsAt = DateTimeOffset.FromUnixTimeSeconds(unixSeconds); }
            catch (ArgumentOutOfRangeException) { resetsAt = null; }
        }

        var remaining = Math.Clamp((100d - used) / 100d, 0d, 1d);
        limits.Add(new UsageLimit(name, remaining, resetsAt, null));
    }

    private static bool TryFindRateLimits(JsonElement element, out JsonElement result)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.NameEquals("rate_limits") && property.Value.ValueKind == JsonValueKind.Object)
                    {
                        result = property.Value;
                        return true;
                    }
                    if (TryFindRateLimits(property.Value, out result)) return true;
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    if (TryFindRateLimits(item, out result)) return true;
                break;
        }

        result = default;
        return false;
    }

    private static UsageSnapshot Fail(UsageProviderDefinition provider, UsageCollectionStatus status, string error) =>
        new(provider.Id, provider.Name, DateTimeOffset.Now, [], Source, status, error);
}
