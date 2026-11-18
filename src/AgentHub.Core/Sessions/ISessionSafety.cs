namespace AgentHub.Sessions;

// ─────────────────────────────────────────────────────────────────────────────
// DRAFT — safety + deterministic detection (design only, no implementation yet).
// The loop that decides "what to input next" and injects it into a live terminal is the
// most dangerous part of the whole system, so its brakes are first-class interfaces —
// not something bolted on later.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Gate every input injection and spawn. Enforces the autonomy level, a command allowlist,
/// the standing "never auto-confirm destructive operations" rule (generalised from the
/// existing fast-forward-only / no-destructive-git constraint), and loop-breakers.
/// </summary>
public interface ISessionActionPolicy
{
    /// <summary>Decide whether an input injection may proceed for the given session state.</summary>
    PolicyDecision EvaluateInput(SendInputRequest request, SessionSnapshot session);

    /// <summary>Decide whether a new session may be spawned (registered repo + known CLI only).</summary>
    PolicyDecision EvaluateSpawn(SpawnSessionRequest request);

    /// <summary>
    /// Loop-breaker: called before each autonomous action. Should deny once a session exceeds
    /// its budget of unattended actions, forcing the orchestrator to surface to a human.
    /// </summary>
    PolicyDecision EvaluateAutonomousBudget(string sessionId);
}

/// <summary>
/// Append-only record of everything the orchestrator (or a human) did to a session, so the
/// feedback loop is auditable and reversible-in-review. Every <see cref="ISessionManager.SendInputAsync"/>
/// and spawn/stop should write here.
/// </summary>
public interface ISessionAuditLog
{
    void Record(string sessionId, string actor, string action, string? detail = null);
}

/// <summary>
/// Deterministic Stage-0 detector. Produces an <see cref="AttentionEvent"/> from cheap,
/// non-model signals — output has been quiet for longer than a window, the tail matches a
/// known prompt, or a headless session emitted a structured "awaiting input" event. This is
/// what keeps the expensive local/power models from having to "constantly infer".
/// </summary>
public interface IAttentionDetector
{
    /// <summary>
    /// Evaluate a session after new output (or a quiescence tick) and optionally raise an event.
    /// Returns null when nothing needs attention.
    /// </summary>
    AttentionEvent? Evaluate(SessionSnapshot session, OutputSlice recent, TimeSpan sinceLastOutput);
}
