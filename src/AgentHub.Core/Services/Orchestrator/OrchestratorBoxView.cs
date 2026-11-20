using AgentHub.Services.Context;

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
    /// <summary>A message from the owner the orchestrator received.</summary>
    Steer,
    /// <summary>The orchestrator put a question to the owner.</summary>
    Question,
    /// <summary>A message AgentHub itself typed into the worker (the handoff prompt).</summary>
    AgentHub,
    /// <summary>The orchestrator's reply to the owner.</summary>
    Reply,
    /// <summary>A note from AgentHub to the owner (a handoff, a held message, the orchestrator failed).</summary>
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
        ExchangeKind.Steer => "You → orchestrator",
        ExchangeKind.Question => "Orchestrator asks you",
        ExchangeKind.Reply => "Orchestrator → you",
        ExchangeKind.Note => "AgentHub",
        _ => "AgentHub → worker",
    };
}

/// <summary>
/// The small orchestrator box shown above the Cockpit pane an orchestrator is attached to (owner feedback 2026-11-05: "I should
/// also be able to see it in the main panel in a smaller box above the active session it is controlling"). Plain terminals
/// never get one. It lists every message read and sent in the current worker session, oldest first (owner feedback 2026-11-06:
/// "show all of the messages received and sent for that session ... forget about anything from the previous handoff"),
/// so it is clear how each message came about.
/// </summary>
public sealed record OrchestratorBoxView(string Title, string State, string Note, IReadOnlyList<OrchestratorExchange> Conversation)
{
    public const string NothingYet = "Tell the orchestrator what the job is and how you want it run. Everything you type here goes to it, not the session; it answers you and instructs the session.";

    /// <summary>The context window fraction at which the session hands off, shown in a field the owner can change while it runs.</summary>
    public double HandoffThreshold { get; init; }

    /// <summary>How much of the context window the session uses now, as "42% of 200k"; null until the first reading.</summary>
    public string? ContextUsed { get; init; }

    /// <summary>The orchestrator's own context window use after its last decision; null until it has decided once.</summary>
    public ContextReading? OrchestratorContext { get; init; }

    /// <summary>The orchestrator follows an advanced template: the box offers its tree (🗺 Run).</summary>
    public bool HasRun { get; init; }

    /// <summary>The run, for the run tree beside the pane; null without one.</summary>
    public TemplateRun? Run { get; init; }

    /// <summary>Where the run stands (<see cref="RunTreeView.Stamp"/>), so the box is shown again whenever a step moves.</summary>
    public string? RunStamp { get; init; }

    /// <summary>The box for an attached pane orchestrator.</summary>
    public static OrchestratorBoxView For(PaneOrchestrator o)
    {
        var state = o.IsWaitingForOwner ? "WAITING FOR YOU" : o.IsHandingOff ? "HANDING OFF" : o.IsThinking ? "THINKING…" : o.Run is { } run ? RunWord(run) : o.Conversation.Count == 0 ? "LISTENING" : "ATTACHED";
        var title = "🧭 " + o.Title + (o.Session > 1 ? $" · session #{o.Session}" : "");
        var used = o.LatestContext is { } c ? $"{Percent.Of(c.UsedFraction)} of {c.WindowTokens / 1000:N0}k" : null;
        // A run's place comes first: it is what the owner looks for; the orchestrator's own line follows it.
        var note = o.Run is { } r ? r.Progress + (o.Note.Length > 0 ? " · " + o.Note : "") : o.Note;
        return new OrchestratorBoxView(title, state, OneLine(note), o.Conversation)
        {
            HandoffThreshold = o.ContextThreshold,
            ContextUsed = used,
            OrchestratorContext = o.OrchestratorContext,
            HasRun = o.Run is not null,
            Run = o.Run,
            RunStamp = o.Run is { } tree ? RunTreeView.Stamp(tree) : null,
        };
    }

    /// <summary>Where a run stands, as the box's state word.</summary>
    public static string RunWord(TemplateRun run) => run.State switch
    {
        RunState.Ready => "RUN READY",
        RunState.Running => "RUNNING",
        RunState.Finished => "RUN FINISHED",
        _ => run.Cause switch
        {
            PauseCause.Owner => "RUN PAUSED",
            PauseCause.Problem => "RUN STOPPED",
            _ => "RUN WAITING FOR YOU",
        },
    };

    /// <summary>
    /// Brings a shown list up to the orchestrator's conversation. The orchestrator replaces its list on every change, so only the
    /// new tail is added; a list that does not continue the shown one (a new worker session, another orchestrator) starts it over.
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
