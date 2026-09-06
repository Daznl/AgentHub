using System.Globalization;
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

        List<FileInfo> files;
        try
        {
            files = new DirectoryInfo(sessionsDir)
                .EnumerateFiles("rollout-*.jsonl", SearchOption.AllDirectories)
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ToList();
        }
        catch (Exception ex)
        {
            return Fail(provider, UsageCollectionStatus.Failed, ex.Message);
        }

        if (files.Count == 0)
            return Fail(provider, UsageCollectionStatus.NotInstalled, "No Codex session files found.");

        // The freshest reading is NOT necessarily in the newest-by-mtime file: any write
        // bumps a file's mtime, and several Codex sessions can be open at once. So pick the
        // reading whose own recorded timestamp is newest. Files are sorted mtime-descending
        // and a reading's timestamp can't be later than its file's mtime, so once a candidate
        // exists we can stop as soon as a file's mtime can no longer beat it.
        RateLimitReading? best = null;
        foreach (var file in files)
        {
            if (best is not null && new DateTimeOffset(file.LastWriteTimeUtc) <= best.Timestamp)
                break;

            var reading = TryReadLastRateLimits(file);
            if (reading is null) continue;

            if (best is null || reading.Timestamp > best.Timestamp)
                best = reading;
        }

        if (best is null)
            return Fail(provider, UsageCollectionStatus.UnsupportedOutput,
                "No rate-limit data recorded in Codex session files yet. Run Codex once to populate.");

        var limits = new List<UsageLimit>();
        AddLimit(limits, best.RateLimits, "primary");
        AddLimit(limits, best.RateLimits, "secondary");

        if (limits.Count == 0)
            return Fail(provider, UsageCollectionStatus.UnsupportedOutput, "Codex rate-limit block had no windows.");

        return new UsageSnapshot(
            provider.Id,
            provider.Name,
            best.Timestamp.LocalDateTime,
            limits,
            Source,
            UsageCollectionStatus.Available,
            RawOutput: best.RateLimits.GetRawText());
    }

    /// <summary>The last rate-limit block found in one session file, tagged with the block's own
    /// recorded UTC timestamp (falling back to the file's write time when none is present).</summary>
    private sealed record RateLimitReading(JsonElement RateLimits, DateTimeOffset Timestamp);

    private static RateLimitReading? TryReadLastRateLimits(FileInfo file)
    {
        JsonElement? lastRateLimits = null;
        DateTimeOffset lastTimestamp = default;
        try
        {
            using var stream = new FileStream(
                file.FullName,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);

            while (reader.ReadLine() is { } line)
            {
                if (!line.Contains("\"rate_limits\"", StringComparison.Ordinal)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    if (TryFindRateLimits(doc.RootElement, out var element) &&
                        element.TryGetProperty("primary", out _))
                    {
                        lastRateLimits = element.Clone();
                        lastTimestamp = TryReadTimestamp(doc.RootElement)
                                        ?? new DateTimeOffset(file.LastWriteTimeUtc);
                    }
                }
                catch (JsonException) { /* skip malformed lines */ }
            }
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        return lastRateLimits is null ? null : new RateLimitReading(lastRateLimits.Value, lastTimestamp);
    }

    private static DateTimeOffset? TryReadTimestamp(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("timestamp", out var ts) &&
            ts.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(ts.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            return parsed;
        }

        return null;
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

        // Codex only records a new rate_limits reading when it makes an API call. If this window's
        // reset time has already passed, the recorded usage is stale: the window has rolled over and
        // is fully available again (zero usage until the next call). Show it as full, and advance the
        // reset to the next window boundary as a best-effort estimate.
        if (resetsAt is { } reset && reset <= DateTimeOffset.Now)
        {
            remaining = 1d;
            if (windowMinutes > 0)
            {
                var step = TimeSpan.FromMinutes(windowMinutes);
                while (reset <= DateTimeOffset.Now) reset += step;
                resetsAt = reset;
            }
            else
            {
                resetsAt = null;
            }
        }

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
