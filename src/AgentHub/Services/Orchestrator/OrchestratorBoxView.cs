namespace AgentHub.Services.Orchestrator;

/// <summary>What one entry in the orchestrator box's conversation is.</summary>
public enum ExchangeKind
{
    /// <summary>A worker reply the orchestrator read.</summary>
    Read,
    /// <summary>A message the orchestrator sent the worker.</summary>
    Sent,
    /// <summary>The orchestrator chose to send nothing yet; the text is its reason.</summary>
    Wait,
    /// <summary>A steer from Daniel the orchestrator received.</summary>
    Steer,
    /// <summary>The orchestrator paused the build for Daniel; the text is its question.</summary>
    Question,
    /// <summary>A message AgentHub itself typed into the worker (handoff, usage wrap-up).</summary>
    AgentHub
}

/// <summary>One message in the current worker session's conversation, as the orchestrator box lists it.</summary>
public sealed record OrchestratorExchange(DateTimeOffset At, ExchangeKind Kind, string Text)
{
    public string Label => Kind switch
    {
        ExchangeKind.Read => "Worker → orchestrator",
        ExchangeKind.Sent => "Orchestrator → worker",
        ExchangeKind.Wait => "Orchestrator waits",
        ExchangeKind.Steer => "Your steer → orchestrator",
        ExchangeKind.Question => "Orchestrator asks you",
        _ => "AgentHub → worker",
    };
}

/// <summary>
/// The small orchestrator box shown above the Cockpit pane an orchestration run is driving (Daniel 2026-11-05: "I should
/// also be able to see it in the main panel in a smaller box above the active session it is controlling"). Plain terminals
/// never get one. It lists every message read and sent in the current worker session, oldest first (Daniel 2026-11-06:
/// "show all of the messages received and sent for that session ... forget about anything from the previous handoff"),
/// so it is clear how each message came about.
/// </summary>
public sealed record OrchestratorBoxView(string Title, string State, string Progress, IReadOnlyList<OrchestratorExchange> Conversation, bool Paused)
{
    public const string NothingYet = "Nothing yet: a new worker is briefed from the spec and the handoff notes.";

    /// <summary>The box for the engine's current state; null when nothing is being orchestrated.</summary>
    public static OrchestratorBoxView? For(OrchestratorState state, bool thinking, string? ideaTitle, int workerSession, string? progress,
        IReadOnlyList<OrchestratorExchange>? conversation)
    {
        if (state == OrchestratorState.Idle || string.IsNullOrWhiteSpace(ideaTitle)) return null;
        var label = state switch
        {
            OrchestratorState.Paused => "PAUSED",
            OrchestratorState.WaitingForReset => "WAITING FOR USAGE RESET",
            OrchestratorState.HandoffPending => "HANDING OFF",
            _ when thinking => "THINKING…",
            _ => "DRIVING",
        };
        var title = workerSession > 0 ? $"{ideaTitle} · worker #{workerSession}" : ideaTitle;
        return new OrchestratorBoxView(title, label, OneLine(progress), conversation ?? [], state == OrchestratorState.Paused);
    }

    private static string OneLine(string? text) => System.Text.RegularExpressions.Regex.Replace(text ?? "", @"\s+", " ").Trim();
}
