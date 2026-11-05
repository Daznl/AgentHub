namespace AgentHub.Services.Orchestrator;

/// <summary>
/// The small orchestrator box shown above the Cockpit pane an orchestration run is driving (Daniel 2026-11-05: "I should
/// also be able to see it in the main panel in a smaller box above the active session it is controlling"). Plain terminals
/// never get one.
/// </summary>
public sealed record OrchestratorBoxView(string Title, string State, string Progress, string? LastSent, bool Paused)
{
    /// <summary>The box for the engine's current state; null when nothing is being orchestrated.</summary>
    public static OrchestratorBoxView? For(OrchestratorState state, bool thinking, string? ideaTitle, int workerSession, string? progress, string? lastSent)
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
        return new OrchestratorBoxView(title, label, OneLine(progress), lastSent is null ? null : OneLine(lastSent), state == OrchestratorState.Paused);
    }

    private static string OneLine(string? text) => System.Text.RegularExpressions.Regex.Replace(text ?? "", @"\s+", " ").Trim();
}
