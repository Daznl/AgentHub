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
    // Percentage of the limit still LEFT (bar is full when you have lots remaining).
    public required string ValueText { get; init; }
    public required string ResetText { get; init; }
    public required double RemainingPercent { get; init; }
    public required MediaBrush BarBrush { get; init; }
}
