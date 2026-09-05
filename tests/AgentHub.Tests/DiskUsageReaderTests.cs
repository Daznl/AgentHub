using System;
using System.IO;
using System.Linq;
using AgentHub.Models;
using AgentHub.Services.Usage;
using Xunit;

namespace AgentHub.Tests;

public class DiskUsageReaderTests
{
    private static UsageProviderDefinition CodexProvider() => new()
    {
        Id = "codex", Name = "Codex", Command = "codex"
    };

    private static UsageProviderDefinition ClaudeProvider() => new()
    {
        Id = "claude", Name = "Claude Code", Command = "claude"
    };

    [Fact]
    public void CodexReader_ParsesPrimaryAndSecondaryFromNewestSession()
    {
        var dir = Path.Combine(Path.GetTempPath(), "agenthub-codex-" + Guid.NewGuid().ToString("N"));
        var nested = Path.Combine(dir, "2026", "09", "05");
        Directory.CreateDirectory(nested);
        try
        {
            // An older file with different numbers, plus the newest with the real values.
            File.WriteAllText(Path.Combine(nested, "rollout-old.jsonl"),
                "{\"type\":\"event\",\"payload\":{\"rate_limits\":{\"primary\":{\"used_percent\":10.0,\"window_minutes\":300,\"resets_at\":1788600000},\"secondary\":{\"used_percent\":5.0,\"window_minutes\":10080,\"resets_at\":1789000000}}}}\n");

            var newestPath = Path.Combine(nested, "rollout-new.jsonl");
            File.WriteAllText(newestPath,
                "{\"type\":\"turn\"}\n" +
                "{\"payload\":{\"info\":{\"rate_limits\":{\"limit_id\":\"codex\",\"primary\":{\"used_percent\":98.0,\"window_minutes\":300,\"resets_at\":1788616339},\"secondary\":{\"used_percent\":31.0,\"window_minutes\":10080,\"resets_at\":1789178892}}}}}\n");
            // Ensure the "new" file is genuinely newest.
            File.SetLastWriteTimeUtc(newestPath, DateTime.UtcNow.AddMinutes(5));

            var snapshot = CodexUsageReader.Read(CodexProvider(), dir);

            Assert.Equal(UsageCollectionStatus.Available, snapshot.Status);
            Assert.Equal(2, snapshot.Limits.Count);

            var fiveHour = snapshot.Limits.Single(l => l.Name == "5-Hour");
            Assert.True(Math.Abs(fiveHour.RemainingFraction - 0.02) < 0.001); // 98% used
            Assert.NotNull(fiveHour.ResetsAt);

            var weekly = snapshot.Limits.Single(l => l.Name == "Weekly");
            Assert.True(Math.Abs(weekly.RemainingFraction - 0.69) < 0.001); // 31% used
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void CodexReader_MissingDirectory_ReportsNotInstalled()
    {
        var snapshot = CodexUsageReader.Read(CodexProvider(),
            Path.Combine(Path.GetTempPath(), "agenthub-does-not-exist-" + Guid.NewGuid().ToString("N")));

        Assert.Equal(UsageCollectionStatus.NotInstalled, snapshot.Status);
        Assert.Empty(snapshot.Limits);
    }

    [Fact]
    public void ClaudeReader_ParsesFiveHourAndSevenDayFromApiResponse()
    {
        // Trimmed shape of GET https://api.anthropic.com/api/oauth/usage
        var json = """
        {
          "five_hour": { "utilization": 34.0, "resets_at": "2026-09-05T16:49:59.910877+00:00" },
          "seven_day": { "utilization": 25.0, "resets_at": "2026-09-07T17:59:59.910930+00:00" },
          "limits": [
            { "kind": "session", "percent": 34, "resets_at": "2026-09-05T16:49:59.910877+00:00" },
            { "kind": "weekly_all", "percent": 25, "resets_at": "2026-09-07T17:59:59.910930+00:00" }
          ]
        }
        """;

        var snapshot = ClaudeUsageReader.ParseUsage(json, ClaudeProvider());

        Assert.Equal(UsageCollectionStatus.Available, snapshot.Status);
        Assert.Equal(2, snapshot.Limits.Count);

        var session = snapshot.Limits.Single(l => l.Name == "Session");
        Assert.True(Math.Abs(session.RemainingFraction - 0.66) < 0.001); // 34% used
        Assert.NotNull(session.ResetsAt);

        var weekly = snapshot.Limits.Single(l => l.Name == "Weekly");
        Assert.True(Math.Abs(weekly.RemainingFraction - 0.75) < 0.001); // 25% used
        Assert.NotNull(weekly.ResetsAt);
    }

    [Fact]
    public void ClaudeReader_UnexpectedResponse_ReportsUnsupported()
    {
        var snapshot = ClaudeUsageReader.ParseUsage("{\"unexpected\":true}", ClaudeProvider());
        Assert.Equal(UsageCollectionStatus.UnsupportedOutput, snapshot.Status);
        Assert.Empty(snapshot.Limits);
    }
}
