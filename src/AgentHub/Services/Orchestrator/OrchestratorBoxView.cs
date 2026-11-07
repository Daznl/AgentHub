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
    /// <summary>A steer from the owner the orchestrator received.</summary>
    Steer,
    /// <summary>The orchestrator paused the build for the owner; the text is its question.</summary>
    Question,
    /// <summary>A message AgentHub itself typed into the worker (handoff, usage wrap-up).</summary>
    AgentHub,
    /// <summary>The orchestrator's reply to the owner (pane orchestrator only: the run orchestrator never talks to the owner).</summary>
    Reply,
    /// <summary>A note from AgentHub to the owner (the orchestrator failed, its limit was reached).</summary>
    Note
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
        ExchangeKind.Reply => "Orchestrator → you",
        ExchangeKind.Note => "AgentHub",
        _ => "AgentHub → worker",
    };
}

/// <summary>
/// The small orchestrator box shown above the Cockpit pane an orchestration run is driving (owner feedback 2026-11-05: "I should
/// also be able to see it in the main panel in a smaller box above the active session it is controlling"). Plain terminals
/// never get one. It lists every message read and sent in the current worker session, oldest first (owner feedback 2026-11-06:
/// "show all of the messages received and sent for that session ... forget about anything from the previous handoff"),
/// so it is clear how each message came about.
/// </summary>
public sealed record OrchestratorBoxView(string Title, string State, string Progress, IReadOnlyList<OrchestratorExchange> Conversation, bool Paused)
{
    public const string NothingYet = "Nothing yet: a new worker is briefed from the spec and the handoff notes.";
    public const string NothingYetAttached = "Tell the orchestrator what the job is and how you want it run. Everything you type here goes to it, not the session; it answers you and instructs the session.";

    /// <summary>
    /// True for a pane orchestrator the owner attached (<see cref="PaneOrchestrator"/>): the box has a message line and a Detach
    /// button, and no Pause or Orchestration window. False for a run orchestrator's box.
    /// </summary>
    public bool Attached { get; init; }

    /// <summary>The box for an attached pane orchestrator.</summary>
    public static OrchestratorBoxView ForAttached(PaneOrchestrator o)
    {
        var state = o.IsThinking ? "THINKING…" : o.Conversation.Count == 0 ? "LISTENING" : "ATTACHED";
        return new OrchestratorBoxView("🧭 " + o.Title, state, OneLine(o.Note), o.Conversation, Paused: false) { Attached = true };
    }

    /// <summary>The box for the engine's current state; null when nothing is being orchestrated.</summary>
    public static OrchestratorBoxView? For(OrchestratorState state, bool thinking, string? ideaTitle, int workerSession, string? progress,
        IReadOnlyList<OrchestratorExchange>? conversation, string? branch = null)
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
        var title = (workerSession > 0 ? $"{ideaTitle} · worker #{workerSession}" : ideaTitle) + (branch is { Length: > 0 } ? $" · worktree {branch}" : "");
        return new OrchestratorBoxView(title, label, OneLine(progress), conversation ?? [], state == OrchestratorState.Paused);
    }

    /// <summary>
    /// Brings a shown list up to the engine's conversation. The engine replaces its list on every change, so only the new
    /// tail is added; a list that does not continue the shown one (a new worker session, another run) starts it over.
    /// True when anything was added or the list started over.
    /// </summary>
    public static bool Sync(System.Collections.ObjectModel.ObservableCollection<OrchestratorExchange> shown, IReadOnlyList<OrchestratorExchange> next)
    {
        var continues = next.Count >= shown.Count && shown.Select((x, i) => ReferenceEquals(x, next[i])).All(same => same);
        var changed = !continues || next.Count > shown.Count;
        if (!continues) shown.Clear();
        foreach (var exchange in next.Skip(shown.Count)) shown.Add(exchange);
        return changed;
    }

    private static string OneLine(string? text) => System.Text.RegularExpressions.Regex.Replace(text ?? "", @"\s+", " ").Trim();
}
