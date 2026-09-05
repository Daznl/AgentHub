namespace AgentHub.Models;

public enum UsageCollectionStatus
{
    Available,
    Stale,
    NotInstalled,
    TimedOut,
    Failed,
    UnsupportedOutput
}

public sealed record UsageLimit(
    string Name,
    double RemainingFraction,
    DateTimeOffset? ResetsAt,
    string? ResetText);

public sealed record UsageSnapshot(
    string ProviderId,
    string ProviderName,
    DateTimeOffset CapturedAt,
    IReadOnlyList<UsageLimit> Limits,
    string Source,
    UsageCollectionStatus Status,
    string? Error = null,
    string? RawOutput = null);
