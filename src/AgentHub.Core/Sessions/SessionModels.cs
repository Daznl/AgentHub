namespace AgentHub.Sessions;

// ─────────────────────────────────────────────────────────────────────────────
// DRAFT — Agentic session layer contract (design only, not yet implemented/wired).
// See docs/AGENTIC_SESSION_LAYER.md for the rationale. These types describe the
// substrate that both the WPF Cockpit UI and an external orchestrator (e.g. the
// on-prem 3-stage agent pipeline) would sit on. No behaviour lives here yet.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Which "plane" a session belongs to.
/// <list type="bullet">
/// <item><see cref="Interactive"/> — human-facing ConPTY + xterm.js terminal (today's Cockpit).</item>
/// <item><see cref="Headless"/> — agent-run session driven programmatically (e.g. Claude
/// <c>--output-format stream-json</c>, Codex <c>exec</c>) that emits structured events
/// instead of a rendered TUI. Preferred for anything an agent controls.</item>
/// </list>
/// </summary>
public enum SessionKind
{
    Interactive,
    Headless
}

/// <summary>Coarse, observable activity state of a session.</summary>
public enum SessionActivity
{
    Starting,
    Busy,           // emitting output / working
    Idle,           // quiescent, no pending prompt
    AwaitingInput,  // sitting at a prompt or an explicit permission request
    Exited,
    Errored
}

/// <summary>
/// How much autonomy the orchestrator is granted over a session. Enforced by
/// <see cref="ISessionActionPolicy"/>, never assumed by callers.
/// </summary>
public enum AutonomyLevel
{
    Observe,        // read-only; agent may watch but not inject input
    Suggest,        // agent proposes input; a human must approve before it is sent
    ActWithConfirm, // agent may send non-destructive input; destructive/confirmation prompts still escalate to a human
    Autonomous      // agent may act within the allowlist; loop-breakers and audit still apply
}

/// <summary>Why the substrate raised an attention event (Stage 0 of the pipeline — deterministic, no LLM).</summary>
public enum AttentionReason
{
    Quiescent,             // output stopped for longer than the quiescence window
    PromptDetected,        // cursor is sitting at a recognised input prompt
    AwaitingConfirmation,  // a headless agent emitted an explicit permission/confirmation request
    Errored,
    Exited
}

/// <summary>Result of attempting to inject input into a session.</summary>
public enum SendOutcome
{
    Delivered,
    RejectedStaleState,  // optimistic-concurrency guard: session moved on since the decision was made
    RejectedByPolicy,    // blocked by autonomy level / allowlist / loop-breaker
    SessionNotFound,
    SessionNotRunning
}

/// <summary>Immutable identity + config of a session.</summary>
public sealed record SessionDescriptor(
    string Id,
    string Title,
    string Cli,              // e.g. "claude", "codex", "pwsh"
    string RepositoryPath,
    SessionKind Kind,
    AutonomyLevel Autonomy,
    DateTimeOffset StartedAt);

/// <summary>
/// A point-in-time view of a session. <see cref="OutputOffset"/> is monotonic (total
/// sanitized characters emitted) and <see cref="StateHash"/> fingerprints the current
/// tail/screen — together they back the delta reads and the send-input staleness guard.
/// </summary>
public sealed record SessionSnapshot(
    SessionDescriptor Descriptor,
    SessionActivity Activity,
    long OutputOffset,
    string StateHash,
    int? ProcessId,
    string? LastError);

/// <summary>
/// A contiguous slice of a session's sanitized output. Returned by
/// <see cref="ISessionManager.ReadSince"/> so an agent reads only what is new since the
/// last decision point ("cut at the previous halt"), not the whole scrollback.
/// </summary>
public sealed record OutputSlice(
    string SessionId,
    long FromOffset,
    long ToOffset,
    string SanitizedText,
    string StateHash);

/// <summary>
/// Deterministic "something changed, look at me" signal. This is Stage 0 of the
/// orchestrator pipeline: it is produced from PTY quiescence / prompt detection /
/// structured headless events — never from a model — so the expensive tiers only wake
/// when there is genuinely something to consider.
/// </summary>
public sealed record AttentionEvent(
    string SessionId,
    AttentionReason Reason,
    long Offset,
    string StateHash,
    DateTimeOffset RaisedAt,
    string? Hint);   // e.g. the detected prompt text or a permission-request summary

/// <summary>
/// A request to inject input into a session. <see cref="ExpectedStateHash"/> enables
/// optimistic concurrency — the substrate rejects the write if the session advanced since
/// the decision was made, preventing "act on a stale screen". <see cref="Actor"/> is
/// recorded for the audit trail.
/// </summary>
public sealed record SendInputRequest(
    string SessionId,
    string Data,
    string Actor,                    // agent id, or "human"
    string? ExpectedStateHash = null,
    bool IsConfirmation = false);    // true when answering a yes/no or permission prompt

public sealed record SendInputResult(
    SendOutcome Outcome,
    long? Offset = null,
    string? Reason = null);

/// <summary>Parameters for launching a new session. Constrained by policy (registered repos + known CLIs only).</summary>
public sealed record SpawnSessionRequest(
    string Cli,
    string RepositoryPath,
    SessionKind Kind = SessionKind.Interactive,
    AutonomyLevel Autonomy = AutonomyLevel.Observe,
    string? Title = null,
    IReadOnlyDictionary<string, string>? Environment = null);

/// <summary>Outcome of a policy evaluation (see <see cref="ISessionActionPolicy"/>).</summary>
public sealed record PolicyDecision(bool Allowed, string? Reason = null);
