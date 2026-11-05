namespace AgentHub.Services.Orchestrator;

/// <summary>
/// The small orchestrator box shown above the Cockpit pane an orchestration run is driving (Daniel 2026-11-05: "I should
/// also be able to see it in the main panel in a smaller box above the active session it is controlling"). Plain terminals
/// never get one. It shows the worker's reply the orchestrator read, then what it sent, so it is clear how each message
/// came about.
/// </summary>
public sealed record OrchestratorBoxView(string Title, string State, string Progress, string LastRead, string? LastSent, bool Paused, string SentLabel = "Last sent: ")
{
    public const string NothingReadYet = "nothing yet: a new worker is briefed from the spec and the handoff notes";

    /// <summary>The box for the engine's current state; null when nothing is being orchestrated.</summary>
    public static OrchestratorBoxView? For(OrchestratorState state, bool thinking, string? ideaTitle, int workerSession, string? progress,
        string? lastRead, string? lastSent, bool sentAnswersRead = true)
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
        var read = string.IsNullOrWhiteSpace(lastRead) ? NothingReadYet : OneLine(lastRead);
        return new OrchestratorBoxView(title, label, OneLine(progress), read, lastSent is null ? null : OneLine(lastSent), state == OrchestratorState.Paused,
            SentLabel: lastSent == null || string.IsNullOrWhiteSpace(lastRead) || sentAnswersRead ? "Last sent: "
                : thinking ? "Last sent (before this reply; deciding the next one): "
                : "Last sent (earlier; it chose to wait): ");
    }

    private static string OneLine(string? text) => System.Text.RegularExpressions.Regex.Replace(text ?? "", @"\s+", " ").Trim();
}
