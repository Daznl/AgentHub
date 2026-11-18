namespace AgentHub.Sessions;

// ─────────────────────────────────────────────────────────────────────────────
// DRAFT — headless session substrate (design only, no implementation yet).
// The four-method contract the orchestrator actually needs is called out inline:
//   (1) sanitized buffer + monotonic offsets   -> ISessionBuffer / SessionSnapshot.OutputOffset
//   (2) deterministic attention event          -> ISessionManager.Attention
//   (3) read_since(offset) delta               -> ISessionManager.ReadSince
//   (4) guarded send_input                      -> ISessionManager.SendInputAsync
// Everything else is session lifecycle needed to make those four useful.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// UI-independent owner of running sessions. The WPF Cockpit becomes one client of this;
/// an external orchestrator (the on-prem agent pipeline) becomes another — neither reaches
/// into the other's ConPTY. Extracting this out of <c>EmbeddedTerminalControl</c> is the
/// first slice to build.
/// </summary>
public interface ISessionManager
{
    // ── Lifecycle / management ────────────────────────────────────────────────
    IReadOnlyList<SessionSnapshot> ListSessions();

    SessionSnapshot? GetSession(string sessionId);

    /// <summary>Launch a new session. Implementations must run the request through
    /// <see cref="ISessionActionPolicy"/> (registered repos + known CLIs only).</summary>
    Task<SessionSnapshot> SpawnAsync(SpawnSessionRequest request, CancellationToken cancellationToken = default);

    Task<bool> StopAsync(string sessionId, CancellationToken cancellationToken = default);

    // ── (3) Delta reads over the (1) sanitized buffer ─────────────────────────
    /// <summary>
    /// Return the sanitized output produced since <paramref name="fromOffset"/>, capped at
    /// <paramref name="maxChars"/>. The returned <see cref="OutputSlice.StateHash"/> is what a
    /// caller passes back into <see cref="SendInputAsync"/> to prove it acted on current state.
    /// </summary>
    OutputSlice ReadSince(string sessionId, long fromOffset, int maxChars = 8192);

    // ── (4) Guarded input ─────────────────────────────────────────────────────
    /// <summary>
    /// Inject input into a session. Returns <see cref="SendOutcome.RejectedStaleState"/> if
    /// <see cref="SendInputRequest.ExpectedStateHash"/> no longer matches (the screen moved on),
    /// or <see cref="SendOutcome.RejectedByPolicy"/> if autonomy/allowlist/loop-breaker forbids it.
    /// </summary>
    Task<SendInputResult> SendInputAsync(SendInputRequest request, CancellationToken cancellationToken = default);

    // ── (2) Attention + lifecycle event streams ───────────────────────────────
    /// <summary>Deterministic Stage-0 signal that a session likely needs a look. No LLM involved.</summary>
    event Action<AttentionEvent>? Attention;

    /// <summary>Fires whenever a session's <see cref="SessionSnapshot.Activity"/> changes.</summary>
    event Action<SessionSnapshot>? SessionStateChanged;
}

/// <summary>
/// Per-session sanitized output store — the single source of truth for a session's text
/// and offsets. Having exactly one of these per session is what lets the watcher and the
/// extractor agree on where "the previous halt" was (no duplicated/dropped context).
/// </summary>
public interface ISessionBuffer
{
    /// <summary>Total sanitized characters appended so far (monotonic).</summary>
    long Offset { get; }

    /// <summary>Fingerprint of the current tail/screen, used for the send-input staleness guard.</summary>
    string StateHash { get; }

    SessionActivity Activity { get; }

    /// <summary>Sanitize (strip ANSI/VT — reuse <c>UsageOutputParser.StripAnsi</c>) and append raw PTY output.</summary>
    long Append(string rawChunk);

    OutputSlice ReadSince(long fromOffset, int maxChars);
}
