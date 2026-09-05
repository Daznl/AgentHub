using MediaBrush = System.Windows.Media.Brush;

namespace AgentHub.Models;

public sealed class UsageProviderDisplay
{
    public required string Name { get; init; }
    public required string StatusText { get; init; }
    public required string SourceText { get; init; }
    public required MediaBrush AccentBrush { get; init; }
    public IReadOnlyList<UsageLimitDisplay> Limits { get; init; } = [];
}

public sealed class UsageLimitDisplay
{
    public required string Name { get; init; }
    // Matches the CLIs' own /status convention: percentage USED (bar fills as you consume).
    public required string ValueText { get; init; }
    public required string ResetText { get; init; }
    public required double UsedPercent { get; init; }
    public required MediaBrush BarBrush { get; init; }
}
