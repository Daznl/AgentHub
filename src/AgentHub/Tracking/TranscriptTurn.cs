namespace AgentHub.Tracking;

public enum TurnStatus { Working, Complete, Interrupted }

/// <summary>
/// One prompt/response exchange in a coding session: what the user asked and the plain-text
/// reply the agent closed with. Tool calls are counted, never stored.
/// </summary>
public sealed class TranscriptTurn
{
    /// <summary>Id of the user prompt entry that opened the turn; stable across re-completions.</summary>
    public required string TurnId { get; init; }
    public string? SessionId { get; init; }
    public required string UserMessage { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? EndedAt { get; set; }
    public TurnStatus Status { get; set; } = TurnStatus.Working;

    /// <summary>Text the agent wrote after its last tool call: normally its closing summary.</summary>
    public string FinalText { get; set; } = "";
    public int ToolCalls { get; set; }
    public List<string> FilesTouched { get; } = [];

    /// <summary>One-sentence summary of <see cref="FinalText"/>, filled in after the turn completes.</summary>
    public string? Summary { get; set; }

    /// <summary>
    /// A copy that later parser updates cannot change. The parser reuses the turn object if the agent carries on,
    /// so anything handled off the parser's thread works on a snapshot.
    /// </summary>
    public TranscriptTurn Snapshot()
    {
        var copy = new TranscriptTurn
        {
            TurnId = TurnId,
            SessionId = SessionId,
            UserMessage = UserMessage,
            StartedAt = StartedAt,
            EndedAt = EndedAt,
            Status = Status,
            FinalText = FinalText,
            ToolCalls = ToolCalls,
            Summary = Summary
        };
        copy.FilesTouched.AddRange(FilesTouched);
        return copy;
    }
}

public enum TurnEventKind
{
    /// <summary>A new user prompt was seen; the agent is now working on it.</summary>
    Started,
    /// <summary>The turn finished (or was cut off by the next prompt). May repeat for the same turn if the agent continues.</summary>
    Completed
}

public sealed record TurnEvent(TurnEventKind Kind, TranscriptTurn Turn);
