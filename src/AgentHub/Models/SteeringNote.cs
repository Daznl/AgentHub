namespace AgentHub.Models;

/// <summary>
/// A piece of direction the user gives a build that is in progress ("focus on the backtest report first").
/// Queued notes are delivered by the orchestrator at the agent's next turn end, or straight away when sent now.
/// </summary>
public sealed class SteeringNote
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string IdeaId { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    /// <summary>When the note was typed into the agent's session; null while it is still queued.</summary>
    public DateTimeOffset? DeliveredAt { get; set; }

    public bool IsDelivered => DeliveredAt is not null;

    public string StatusText => DeliveredAt is { } at ? $"delivered {at:HH:mm}" : $"queued {CreatedAt:HH:mm}";
}
