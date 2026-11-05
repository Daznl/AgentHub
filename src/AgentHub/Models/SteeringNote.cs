namespace AgentHub.Models;

/// <summary>
/// A piece of direction the user gives a build that is in progress ("focus on the backtest report first").
/// Notes go to the orchestrator (never straight to the worker): at the worker's next turn end, or straight away when urgent.
/// </summary>
public sealed class SteeringNote
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string IdeaId { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    /// <summary>When the note was handed to the orchestrator; null while it is still queued.</summary>
    public DateTimeOffset? DeliveredAt { get; set; }

    public bool IsDelivered => DeliveredAt is not null;

    public string StatusText => DeliveredAt is { } at ? $"to orchestrator {at:HH:mm}" : $"queued {CreatedAt:HH:mm}";
}
