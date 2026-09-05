using AgentHub.Models;
using AgentHub.Services;
using AgentHub.Services.Usage;
using Xunit;
using Xunit.Abstractions;

namespace AgentHub.Tests;

public class LiveUsageServiceTests
{
    private readonly ITestOutputHelper _output;

    public LiveUsageServiceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task Live_AntigravityUsage_ReturnsAvailableSnapshots()
    {
        var runner = new ProcessRunner();
        var usageService = new UsageService(runner);

        var provider = new UsageProviderDefinition
        {
            Id = "gemini",
            Name = "Antigravity",
            Command = "agy",
            Arguments = ["-p", "/usage"],
            Enabled = true
        };

        var snapshots = await usageService.RefreshAsync([provider]);
        Assert.Single(snapshots);

        var snap = snapshots[0];
        _output.WriteLine($"Provider: {snap.ProviderName}, Status: {snap.Status}, Limits: {snap.Limits.Count}");
        _output.WriteLine($"RawOutput:\n{snap.RawOutput}");

        Assert.Equal(UsageCollectionStatus.Available, snap.Status);
        Assert.NotEmpty(snap.Limits);
    }

    [Fact]
    public async Task Live_CodexBackgroundTerminal_CapturesOutput()
    {
        var runner = new ProcessRunner();
        var usageService = new UsageService(runner);

        var provider = new UsageProviderDefinition
        {
            Id = "codex",
            Name = "Codex",
            Command = "codex",
            Arguments = ["/status"],
            UnqualifiedPercentagesAreRemaining = true,
            Enabled = true
        };

        var snapshots = await usageService.RefreshAsync([provider]);
        Assert.Single(snapshots);

        var snap = snapshots[0];
        _output.WriteLine($"Provider: {snap.ProviderName}, Status: {snap.Status}, Limits: {snap.Limits.Count}");
        _output.WriteLine($"Source: {snap.Source}");
        _output.WriteLine($"Error: {snap.Error}");
        _output.WriteLine($"RawOutput:\n{snap.RawOutput}");

        Assert.NotNull(snap.RawOutput);
    }

    [Fact]
    public async Task Live_ClaudeBackgroundTerminal_CapturesOutput()
    {
        var runner = new ProcessRunner();
        var usageService = new UsageService(runner);

        var provider = new UsageProviderDefinition
        {
            Id = "claude",
            Name = "Claude Code",
            Command = "claude",
            Arguments = ["/status"],
            Enabled = true
        };

        var snapshots = await usageService.RefreshAsync([provider]);
        Assert.Single(snapshots);

        var snap = snapshots[0];
        _output.WriteLine($"Provider: {snap.ProviderName}, Status: {snap.Status}, Limits: {snap.Limits.Count}");
        _output.WriteLine($"Source: {snap.Source}");
        _output.WriteLine($"Error: {snap.Error}");
        _output.WriteLine($"RawOutput:\n{UsageOutputParser.StripAnsi(snap.RawOutput ?? "")}");
    }
}