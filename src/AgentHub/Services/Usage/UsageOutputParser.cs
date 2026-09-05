using System.Globalization;
using System.Text.RegularExpressions;
using AgentHub.Models;

namespace AgentHub.Services.Usage;

public static partial class UsageOutputParser
{
    [GeneratedRegex(@"(?:\x1B\[|\[)(?<count>\d+)?C")]
    private static partial Regex CursorForwardRegex();

    [GeneratedRegex(@"(?:\x1B\[|\[)(?<col>\d+)?G")]
    private static partial Regex CursorHorizontalRegex();

    [GeneratedRegex(@"\x1B(?:\][^\x07\x1B]*(?:\x07|\x1B\\)|\[[0-?]*[ -/]*[@-~]|[@-Z\\-_])")]
    private static partial Regex AnsiRegex();

    [GeneratedRegex(@"\]0;[^\r\n]*")]
    private static partial Regex OrphanOscRegex();

    [GeneratedRegex(@"\[(?:\?[\d;]+[a-zA-Z]|\d+[a-zA-Z]|\d+;\d+[a-zA-Z]|m|H|K|\d+\s+q)")]
    private static partial Regex OrphanCsiRegex();

    public static string StripAnsi(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var s = text.Replace('\r', '\n');
        // Replace cursor-forward with equivalent spaces so text across columns or formatted spans isn't squished
        s = CursorForwardRegex().Replace(s, match =>
        {
            if (match.Groups["count"].Success && int.TryParse(match.Groups["count"].Value, out var n))
            {
                return new string(' ', Math.Clamp(n, 1, 80));
            }
            return " ";
        });
        s = CursorHorizontalRegex().Replace(s, " ");
        s = AnsiRegex().Replace(s, "");
        s = OrphanOscRegex().Replace(s, "");
        s = OrphanCsiRegex().Replace(s, "");
        return s;
    }

    public static string ExtractRelevantScreen(string text, string providerId)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var clean = StripAnsi(text);
        var provider = providerId.ToLowerInvariant();

        if (provider.Contains("claude"))
        {
            // If the usage stats have rendered
            var sessionIndex = clean.LastIndexOf("Current session", StringComparison.OrdinalIgnoreCase);
            if (sessionIndex >= 0)
            {
                var prefixSearch = clean[..sessionIndex];
                var headerIndex = prefixSearch.LastIndexOf("\nSession", StringComparison.OrdinalIgnoreCase);
                if (headerIndex >= 0 && (sessionIndex - headerIndex) < 600)
                {
                    return clean[(headerIndex + 1)..].Trim();
                }

                var costIndex = prefixSearch.LastIndexOf("Total cost:", StringComparison.OrdinalIgnoreCase);
                if (costIndex >= 0 && (sessionIndex - costIndex) < 600)
                {
                    var lineStart = clean.LastIndexOf('\n', costIndex);
                    return clean[(lineStart >= 0 ? lineStart + 1 : costIndex)..].Trim();
                }

                var lineStartSession = clean.LastIndexOf('\n', sessionIndex);
                return clean[(lineStartSession >= 0 ? lineStartSession + 1 : sessionIndex)..].Trim();
            }

            // If still loading stats from Anthropic API
            var loadingIndex = clean.LastIndexOf("Loading your Claude Code stats", StringComparison.OrdinalIgnoreCase);
            if (loadingIndex >= 0)
            {
                var lineStart = clean.LastIndexOf('\n', loadingIndex);
                return clean[(lineStart >= 0 ? lineStart + 1 : loadingIndex)..].Trim();
            }
        }
        else if (provider.Contains("codex"))
        {
            var limitIndex = clean.LastIndexOf("5h limit", StringComparison.OrdinalIgnoreCase);
            if (limitIndex < 0)
            {
                limitIndex = clean.LastIndexOf("Weekly limit", StringComparison.OrdinalIgnoreCase);
            }

            if (limitIndex >= 0)
            {
                var prefixSearch = clean[..limitIndex];
                var modelIndex = prefixSearch.LastIndexOf("Model:", StringComparison.OrdinalIgnoreCase);
                if (modelIndex >= 0 && (limitIndex - modelIndex) < 200)
                {
                    var lineStart = clean.LastIndexOf('\n', modelIndex);
                    return clean[(lineStart >= 0 ? lineStart + 1 : modelIndex)..].Trim();
                }

                var lineStartLimit = clean.LastIndexOf('\n', limitIndex);
                return clean[(lineStartLimit >= 0 ? lineStartLimit + 1 : limitIndex)..].Trim();
            }
        }

        return clean.Trim();
    }

    [GeneratedRegex(@"(?<percent>\d{1,3}(?:\.\d+)?)\s*%", RegexOptions.IgnoreCase)]
    private static partial Regex PercentRegex();

    [GeneratedRegex(@"(?:resets?|reset\s+at|refreshes?\s+in|refreshes?\s+at)\s*[:]?\s*(?<reset>[^|\r\n]+)", RegexOptions.IgnoreCase)]
    private static partial Regex ResetRegex();

    [GeneratedRegex(@"(?<iso>\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?Z)")]
    private static partial Regex IsoDateRegex();

    public static IReadOnlyList<UsageLimit> Parse(string output, UsageProviderDefinition provider)
    {
        var clean = StripAnsi(output);
        var rawLines = clean.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var limits = new List<UsageLimit>();
        string? previousLabel = null;

        for (var index = 0; index < rawLines.Length; index++)
        {
            var rawLine = rawLines[index];
            var line = NormalizeLine(rawLine);
            if (string.IsNullOrWhiteSpace(line)) continue;

            // 1. Check for tab-separated lines (e.g. agy -p "/usage")
            var tabParts = rawLine.Split('\t', StringSplitOptions.TrimEntries);
            if (tabParts.Length >= 3 && PercentRegex().IsMatch(tabParts[2]))
            {
                var percentMatchTab = PercentRegex().Match(tabParts[2]);
                if (double.TryParse(percentMatchTab.Groups["percent"].Value, NumberStyles.Float,
                        CultureInfo.InvariantCulture, out var tabPercentage) && tabPercentage is >= 0 and <= 100)
                {
                    var isRemaining = tabParts[1].Contains("remaining", StringComparison.OrdinalIgnoreCase) ||
                                     (!tabParts[1].Contains("used", StringComparison.OrdinalIgnoreCase) && provider.UnqualifiedPercentagesAreRemaining);
                    var remainingTab = isRemaining ? tabPercentage / 100d : 1d - tabPercentage / 100d;
                    var resetRaw = tabParts.Length >= 4 ? tabParts[3] : null;
                    var groupPrefix = tabParts[0].Contains("claude", StringComparison.OrdinalIgnoreCase) ? "Claude/GPT " :
                                      tabParts[0].Contains("gemini", StringComparison.OrdinalIgnoreCase) ? "Gemini " : "";
                    var baseName = CanonicalLabel(tabParts[1]);
                    var limitName = $"{groupPrefix}{baseName}".Trim();

                    limits.Add(new UsageLimit(
                        limitName,
                        Math.Clamp(remainingTab, 0d, 1d),
                        ParseReset(resetRaw),
                        FormatResetText(resetRaw)));
                    continue;
                }
            }

            // 2. Standard regex percentage parsing
            var percentMatch = PercentRegex().Match(line);
            if (!percentMatch.Success)
            {
                if (LooksLikeLabel(line)) previousLabel = line;
                continue;
            }

            if (!double.TryParse(percentMatch.Groups["percent"].Value, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var percentage) || percentage is < 0 or > 100)
            {
                continue;
            }

            var textBeforePercent = line[..percentMatch.Index];
            var explicitRemaining = ContainsAny(line, "left", "remaining", "available");
            var explicitUsed = ContainsAny(line, "used", "consumed", "utilization");
            var remaining = explicitRemaining || (!explicitUsed && provider.UnqualifiedPercentagesAreRemaining)
                ? percentage / 100d
                : 1d - percentage / 100d;

            var rawLabel = CleanLabel(textBeforePercent);
            if (string.IsNullOrWhiteSpace(rawLabel) || rawLabel.Length < 2)
                rawLabel = previousLabel ?? "Usage limit";

            if (!IsSupportedLabel(rawLabel, provider.Id)) continue;

            var resetText = FindReset(rawLines, index);
            limits.Add(new UsageLimit(
                CanonicalLabel(rawLabel),
                Math.Clamp(remaining, 0d, 1d),
                ParseReset(resetText),
                FormatResetText(resetText)));
        }

        return limits
            .GroupBy(limit => limit.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .ToList();
    }

    private static string? FindReset(string[] lines, int index)
    {
        for (var i = index; i < Math.Min(lines.Length, index + 3); i++)
        {
            var line = NormalizeLine(lines[i]);
            var isoMatch = IsoDateRegex().Match(line);
            if (isoMatch.Success) return isoMatch.Groups["iso"].Value;

            var match = ResetRegex().Match(line);
            if (match.Success) return match.Groups["reset"].Value.Trim().TrimEnd('.', ')');
        }
        return null;
    }

    private static DateTimeOffset? ParseReset(string? resetText)
    {
        if (string.IsNullOrWhiteSpace(resetText)) return null;

        var isoMatch = IsoDateRegex().Match(resetText);
        if (isoMatch.Success && DateTimeOffset.TryParse(isoMatch.Value, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var isoTime))
        {
            return isoTime;
        }

        if (long.TryParse(resetText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unixSeconds))
        {
            try { return DateTimeOffset.FromUnixTimeSeconds(unixSeconds); }
            catch (ArgumentOutOfRangeException) { return null; }
        }

        if (!Regex.IsMatch(resetText, @"\b\d{4}\b")) return null;
        return DateTimeOffset.TryParse(resetText, CultureInfo.CurrentCulture,
            DateTimeStyles.AssumeLocal, out var value) ? value : null;
    }

    private static string? FormatResetText(string? resetText)
    {
        if (string.IsNullOrWhiteSpace(resetText)) return null;
        var trimmed = resetText.Trim().TrimEnd('.', ')');
        var parsed = ParseReset(trimmed);
        if (parsed.HasValue)
        {
            return parsed.Value.ToLocalTime().ToString("ddd h:mm tt");
        }
        return trimmed;
    }

    private static string NormalizeLine(string value) => value
        .Replace('│', ' ')
        .Replace('┃', ' ')
        .Replace('║', ' ')
        .Replace('─', ' ')
        .Replace('━', ' ')
        .Trim();

    private static bool LooksLikeLabel(string line) =>
        line.Length <= 80 && ContainsAny(line, "limit", "session", "week", "quota", "pro", "flash", "models");

    private static string CleanLabel(string label)
    {
        label = PercentRegex().Replace(label, "");
        label = Regex.Replace(label, @"[^\p{L}\p{N}\s\-_/.]+", " ");
        label = Regex.Replace(label, @"\b(used|left|remaining|available|usage|limit)\b", " ", RegexOptions.IgnoreCase);
        return Regex.Replace(label, @"\s+", " ").Trim(' ', '-', ':', '·');
    }

    private static string CanonicalLabel(string label)
    {
        var lower = label.ToLowerInvariant();
        if (lower.Contains("5h") || lower.Contains("5 hour") || lower.Contains("five hour")) return "5-Hour";
        if (lower.Contains("session")) return "Session";
        if (lower.Contains("7d") || lower.Contains("7 day") || lower.Contains("week")) return "Weekly";
        if (lower.Contains("flash lite")) return "Flash Lite";
        if (lower.Contains("flash")) return "Flash";
        if (lower.Contains("pro")) return "Pro";
        return CultureInfo.CurrentCulture.TextInfo.ToTitleCase(label.ToLower());
    }

    private static bool IsSupportedLabel(string label, string providerId)
    {
        var common = ContainsAny(label, "limit", "quota");
        return providerId.ToLowerInvariant() switch
        {
            "codex" => common || ContainsAny(label, "5h", "5 hour", "week"),
            "claude" => common || ContainsAny(label, "session", "5h", "5 hour", "week", "7d", "7 day"),
            "gemini" => common || ContainsAny(label, "gemini", "pro", "flash", "models", "hour", "week"),
            _ => common
        };
    }

    private static bool ContainsAny(string value, params string[] terms) =>
        terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));
}
