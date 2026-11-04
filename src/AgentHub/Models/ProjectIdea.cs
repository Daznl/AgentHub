namespace AgentHub.Models;

public enum IdeaStatus
{
    Queued,
    Building,
    WaitingForReset,
    Completed,
    Paused
}

/// <summary>
/// An application or feature idea queued for autonomous building.
/// Tracks requirements, target directory, context handoff threshold, and execution state.
/// </summary>
public sealed class ProjectIdea
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string TargetDirectory { get; set; } = string.Empty;
    public string? GitHubUrl { get; set; }
    public string AgentCommand { get; set; } = "claude";

    /// <summary>Context window fraction (0.50 - 0.95) that triggers an automated handoff turn.</summary>
    public double ContextHandoffThreshold { get; set; } = 0.75;

    public IdeaStatus Status { get; set; } = IdeaStatus.Queued;
    public int HandoffCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? LastActiveAt { get; set; }
    public string? LastHandoffSummary { get; set; }
    public string? StatusNote { get; set; }

    public string StatusBadgeText => Status switch
    {
        IdeaStatus.Building => "BUILDING",
        IdeaStatus.WaitingForReset => "WAITING FOR RESET",
        IdeaStatus.Completed => "COMPLETED",
        IdeaStatus.Paused => "PAUSED",
        _ => "QUEUED"
    };

    public string StatusBadgeBrush => Status switch
    {
        IdeaStatus.Building => "#10B981",       // green
        IdeaStatus.WaitingForReset => "#F59E0B", // amber
        IdeaStatus.Completed => "#3B82F6",      // blue
        IdeaStatus.Paused => "#6B7280",         // gray
        _ => "#8B5CF6"                          // purple
    };
}
