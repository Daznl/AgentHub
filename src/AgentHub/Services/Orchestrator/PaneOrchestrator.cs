using System.IO;
using System.Text;
using System.Text.Json;
using AgentHub.Services.Context;

namespace AgentHub.Services.Orchestrator;

/// <summary>The pane orchestrator's one output per message: a word for the owner, a message for the worker, or both.</summary>
public sealed record PaneDecision(string? ToOwner, string? ToWorker, string Note)
{
    public const string Schema = """
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "to_owner": { "type": ["string", "null"], "description": "Your reply to the owner, shown in AgentHub. Null when there is nothing to tell them now." },
            "to_worker": { "type": ["string", "null"], "description": "The exact message to type into the worker session. Null to send it nothing now." },
            "note": { "type": "string", "description": "One line on where things stand, shown under the box title." }
          },
          "required": ["to_owner", "to_worker", "note"]
        }
        """;

    public static PaneDecision Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new FormatException("The reply must be a JSON object.");
        string? Str(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()?.Trim() : null;
        var owner = Str("to_owner");
        var worker = Str("to_worker");
        return new PaneDecision(string.IsNullOrWhiteSpace(owner) ? null : owner, string.IsNullOrWhiteSpace(worker) ? null : worker, Str("note") ?? "");
    }
}

/// <summary>Answers one message from the pane orchestrator (tests script it).</summary>
public interface IPaneBrain
{
    Task<PaneDecision> DecideAsync(string message, CancellationToken cancellationToken);

    /// <summary>The brain's own context window use after its last decision; null when unknown (scripted brains).</summary>
    ContextReading? ReadContext() => null;
}

/// <summary>The real brain: a <see cref="ClaudeJsonSession"/> answering with <see cref="PaneDecision"/>s.</summary>
public sealed class ClaudePaneBrain(ClaudeJsonSession session, long assumedWindowTokens = 200_000) : IPaneBrain
{
    public async Task<PaneDecision> DecideAsync(string message, CancellationToken cancellationToken) =>
        (await session.AskAsync(message, PaneDecision.Parse, cancellationToken)).Value;

    public ContextReading? ReadContext() => session.ReadContext(assumedWindowTokens);
}

/// <summary>
/// An orchestrator the owner puts on a running Cockpit session with one click, and takes off again as freely (owner feedback
/// 2026-11-07: the run orchestrator is "a bit too formal"; "I want to add orchestration freely onto a currently running
/// session ... all inputs just go to the orchestrator and the orchestrator reply"). Nothing is started or recorded: the
/// session already runs and stays the owner's. The orchestrator sits between them: every message the owner types into the
/// box goes to it, it replies to the owner and/or instructs the worker, and every worker turn end is shown to it too.
/// Its context is whatever the owner tells it (its standing instructions) plus, optionally, a workflow's instructions.
/// When the session's context window fills to <see cref="ContextThreshold"/>, AgentHub has the session write
/// <c>.agenthub/handoff.md</c>, ends it and starts a fresh one with the same command in the same pane, which the orchestrator
/// briefs from the handoff. No usage checks, stages or run archive: a thin loop on purpose.
/// </summary>
public sealed class PaneOrchestrator : IDisposable
{
    private readonly IBuildWorker _worker;
    private readonly IPaneBrain _brain;
    private readonly string? _projectDirectory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly object _conversationLock = new();
    private volatile IReadOnlyList<OrchestratorExchange> _conversation = [];
    private bool _introduced;
    private bool _thinking;
    private bool _turnEndedWhileThinking;
    private bool _sentSinceLastTurn;
    private string? _lastReplyHandled;
    private DateTimeOffset _lastSubmitAt = DateTimeOffset.MinValue;
    private DateTimeOffset? _transcriptTurnHandled;
    private int _decisions;

    // ---- Handoff: a fresh session when the context window fills ----
    private bool _handoffRequested;
    private DateTimeOffset _handoffRequestedAt;
    private bool _restarting;
    private bool _expectExit;
    private int _session = 1;
    private DateTimeOffset _sessionStartedAt = DateTimeOffset.MinValue;
    private double? _contextBaseline;

    /// <summary>What the orchestrator is for, in the owner's words: shown in the box title.</summary>
    public string Title { get; }
    /// <summary>The workflow whose instructions were added to the orchestrator's context, if any.</summary>
    public OrchestratorWorkflow? Workflow { get; }
    public bool IsThinking => _thinking;
    /// <summary>The session shows a security check (folder trust, a permission dialog) that only the owner answers; the orchestrator types nothing until then.</summary>
    public bool IsWaitingForOwner => _worker.IsRunning && _worker.IsAtSecurityCheck;
    /// <summary>A message for the session held back while a security check is on screen; typed in once the owner has answered.</summary>
    public string? HeldForWorker => _held;
    private string? _held;
    /// <summary>The session has been asked to write its handoff; a fresh session follows when its turn ends.</summary>
    public bool IsHandingOff => _handoffRequested || _restarting;
    /// <summary>
    /// True from ending the old session to starting the fresh one: the pane's session ends on purpose, so the owner of this
    /// object must not take that for the session ending and detach the orchestrator.
    /// </summary>
    public bool IsRestarting => _restarting;
    public string Note { get; private set; } = "";
    public int Decisions => _decisions;
    /// <summary>The session number in this pane since the orchestrator was attached (1 for the session it was put on).</summary>
    public int Session => _session;
    /// <summary>The conversation so far, oldest first. Replaced, never changed in place, so any thread gets a whole list.</summary>
    public IReadOnlyList<OrchestratorExchange> Conversation => _conversation;

    /// <summary>Anything shown changed (thinking, note, conversation, handoff). From any thread.</summary>
    public event Action? Changed;

    /// <summary>A turn end counts once the worker has been quiet this long (it may print a little after its prompt returns).</summary>
    public TimeSpan TurnSettle { get; set; } = TimeSpan.FromSeconds(15);
    public TimeSpan LoopInterval { get; set; } = TimeSpan.FromSeconds(3);
    /// <summary>Upper bound on decisions, so a loop between the two sessions cannot run away.</summary>
    public int MaxDecisions { get; set; } = 300;
    /// <summary>
    /// Context window fraction at which the session is handed off to a fresh one. Changeable while the orchestrator runs
    /// (the box above the pane has a field for it): the latest reading is checked against the new limit at once, so lowering
    /// it below the session's current size hands off now.
    /// </summary>
    public double ContextThreshold
    {
        get => _contextThreshold;
        set
        {
            var next = Math.Clamp(value, MinContextThreshold, MaxContextThreshold);
            if (Math.Abs(next - _contextThreshold) < 0.0005) return;
            _contextThreshold = next;
            if (!_constructed) return; // the workflow's limit, set before anything runs: nothing to note or check yet
            AddExchange(ExchangeKind.Note, $"Handoff limit set to {next:P0} of the context window.");
            OnContextChanged(_worker.LatestContextReading);
        }
    }
    private double _contextThreshold = 0.75;
    private bool _constructed;
    public const double MinContextThreshold = 0.40;
    public const double MaxContextThreshold = 0.95;

    /// <summary>The worker's latest context reading, for the box (null until the first one).</summary>
    public ContextReading? LatestContext => _worker.LatestContextReading;
    /// <summary>A handoff turn that takes longer than this is cut short and the fresh session started anyway.</summary>
    public TimeSpan HandoffTimeout { get; set; } = TimeSpan.FromMinutes(10);
    /// <summary>Time for a fresh CLI to show its prompt before its first instruction is typed.</summary>
    public TimeSpan StartupDelay { get; set; } = TimeSpan.FromSeconds(4);
    /// <summary>Time for the old CLI to go after it was stopped.</summary>
    public TimeSpan StopDelay { get; set; } = TimeSpan.FromMilliseconds(2500);

    public PaneOrchestrator(IBuildWorker worker, IPaneBrain brain, string title, OrchestratorWorkflow? workflow = null, string? projectDirectory = null)
    {
        _worker = worker;
        _brain = brain;
        Title = title;
        Workflow = workflow;
        _projectDirectory = projectDirectory;
        if (workflow?.ContextThreshold is { } t) ContextThreshold = t;
        _constructed = true;
        _worker.TurnEnded += OnWorkerTurnEnded;
        _worker.ContextChanged += OnContextChanged;
        _worker.Exited += OnWorkerExited;
        _ = LoopAsync();
    }

    /// <summary>The orchestrator CLI. AGENTHUB_ORCHESTRATOR_COMMAND replaces it, for UI tests that use a scripted orchestrator.</summary>
    public static string DefaultCommand =>
        Environment.GetEnvironmentVariable("AGENTHUB_ORCHESTRATOR_COMMAND") is { Length: > 0 } c ? c : "claude";

    /// <summary>Where the orchestrator's own Claude session runs (its transcript), apart from the project's sessions.</summary>
    public static string DefaultSessionsRoot =>
        Environment.GetEnvironmentVariable("AGENTHUB_PANE_ORCHESTRATORS_DIR") is { Length: > 0 } d ? d : @"C:\data\agenthub\pane-orchestrators";

    /// <summary>The real thing for a Cockpit pane: a Claude Code session on the owner's login reading the pane's folder.</summary>
    /// <param name="assumedWindowTokens">The orchestrator's own context window, for its bar (Claude Code does not record it).</param>
    public static PaneOrchestrator ForPane(IBuildWorker worker, ProcessRunner runner, string command, string model, string projectDirectory,
        string paneLabel, OrchestratorWorkflow? workflow, string? sessionsRoot = null, long assumedWindowTokens = 200_000)
    {
        var folder = Path.Combine(sessionsRoot ?? DefaultSessionsRoot, $"{DateTimeOffset.Now:yyyyMMdd-HHmmss}-{OrchestratorMessages.Slug(Path.GetFileName(projectDirectory.TrimEnd('\\', '/')))}");
        var session = new ClaudeJsonSession(runner, command, workflow?.Model ?? model, folder, projectDirectory, SystemPrompt(projectDirectory, paneLabel, workflow), PaneDecision.Schema);
        return new PaneOrchestrator(worker, new ClaudePaneBrain(session, assumedWindowTokens), workflow?.Name ?? "Orchestrator", workflow, projectDirectory);
    }

    /// <summary>The orchestrator's own context window use, read from its transcript after each decision; null until it has decided once.</summary>
    public ContextReading? OrchestratorContext => _orchestratorContext;
    private ContextReading? _orchestratorContext;

    // ---- Prompts ----

    public static string SystemPrompt(string projectDirectory, string paneLabel, OrchestratorWorkflow? workflow)
    {
        var core = $"""
            You are the ORCHESTRATOR attached to a running Claude Code session, the WORKER, in {projectDirectory} (AgentHub pane "{paneLabel}").
            The owner, the person using AgentHub, has put you between themselves and the worker: everything they type comes to you,
            not the worker, and your replies are shown to them. You direct the worker; you never do the work yourself.

            How this works:
            - Every message comes from AgentHub, labelled: [EVENT] what happened, [FROM OWNER] what the owner wrote, [WORKER FINAL REPLY]
              the worker's last message, [WORKER SO FAR] where the session stood when you were attached, [HANDOFF.MD] the worker's notes
              file after a handoff, [STATUS] numbers.
            - The owner's messages are both the job and your standing instructions. When they tell you what the work is, how to behave,
              what to watch for or what to pass on, keep to it for the rest of the session. There may be no written spec: their messages are the spec.
            - to_owner: your reply to the owner. Short and direct, no padding; answer what they asked, report what they should know.
              Null when there is nothing to say.
            - to_worker: the next instruction for the worker. One clear, concrete step or small batch of steps, building on what it just
              reported. The worker only sees to_worker and cannot see this conversation; write it as a direct instruction and never quote
              these labels to it. Null to send nothing (the worker is mid-task, or the next step is the owner's to decide).
            - Both at once is normal: pass the owner's wish to the worker and tell the owner what you did.
            - When the worker finishes a turn: judge its reply, tell the owner what matters in a line or two, and either give the worker
              its next step or leave it idle and ask the owner, whichever the standing instructions call for.
            - If the worker asks a question that the owner's instructions already answer, answer it yourself; otherwise put it to the owner.
            - Handoffs: when the worker's context window fills, AgentHub has it write .agenthub/handoff.md, ends that session and starts a
              fresh one with an empty memory in the same pane. You are then asked for the fresh session's first instruction: it must start
              by reading .agenthub/handoff.md, and you carry over the job and the owner's standing instructions in your own words.
            - You can read the project folder (Read, Grep, Glob) to check claims; keep that brief.

            Your only output is the JSON: to_owner, to_worker, note (one line on where things stand).
            """;
        if (workflow == null) return core;
        return core + $"""


            ===== CONTEXT: {workflow.Name} =====
            {workflow.Description}

            {workflow.ResolvedInstructions()}
            """;
    }

    /// <summary>One message for the orchestrator.</summary>
    public static string Compose(string eventText, string? fromOwner, string? workerReply, string? soFar, string? status, string? handoff = null)
    {
        var sb = new StringBuilder();
        sb.Append("[EVENT] ").Append(eventText).Append("\n\n");
        if (!string.IsNullOrWhiteSpace(soFar)) sb.Append("[WORKER SO FAR]\n").Append(soFar.Trim()).Append("\n\n");
        if (!string.IsNullOrWhiteSpace(status)) sb.Append("[STATUS] ").Append(status.Trim()).Append("\n\n");
        if (!string.IsNullOrWhiteSpace(handoff)) sb.Append("[HANDOFF.MD]\n").Append(OrchestratorMessages.Cut(handoff.Trim(), OrchestratorMessages.MaxHandoffChars, keepTail: false)).Append("\n\n");
        if (workerReply is not null)
            sb.Append("[WORKER FINAL REPLY]\n")
              .Append(workerReply.Length == 0 ? "(the worker ended its turn without a text reply)" : OrchestratorMessages.Cut(workerReply.Trim(), OrchestratorMessages.MaxWorkerReplyChars, keepTail: true))
              .Append("\n\n");
        if (!string.IsNullOrWhiteSpace(fromOwner)) sb.Append("[FROM OWNER]\n").Append(fromOwner.Trim()).Append("\n\n");
        sb.Append("Reply with the JSON.");
        return sb.ToString();
    }

    // ---- Owner messages ----

    /// <summary>The owner typed into the box: the orchestrator gets it now, whether the worker is idle or mid-turn.</summary>
    public async Task SendAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        AddExchange(ExchangeKind.Steer, text.Trim());
        var busy = _worker.IsRunning && !_worker.IsAwaitingInput;
        var eventText = IsHandingOff
            ? "owner_message: the owner wrote to you. The worker is handing off (writing its notes before a fresh session starts): hold any instruction for the fresh session; answer the owner."
            : busy
            ? "owner_message: the owner wrote to you. The worker is mid-turn: a to_worker is typed in now and the worker reads it at its next step."
            : "owner_message: the owner wrote to you. The worker is idle at its prompt.";
        await AskAsync(eventText, fromOwner: text, workerReply: null);
    }

    // ---- Worker turn ends ----

    private void OnWorkerTurnEnded() => _ = HandleTurnEndAsync();

    private async Task HandleTurnEndAsync(bool seenInTranscript = false)
    {
        if (_disposeCts.IsCancellationRequested || _restarting) return;
        if (_worker.IsAtSecurityCheck) { NoteWaitingForOwner(); return; } // a dialog, not a turn end: the owner answers it, then the turn goes on
        if (_thinking) { _turnEndedWhileThinking = true; return; }
        if (!seenInTranscript)
        {
            if (!await SettleAsync()) return;
            _transcriptTurnHandled = _worker.ReadTurnEndedAt();
        }
        if (_handoffRequested)
        {
            await PerformHandoffCycleAsync("the session finished its handoff turn");
            return;
        }
        var reply = _worker.ReadLastReply();
        // The reply the orchestrator already read: the terminal went quiet (a long tool run, a dialog) before the worker's next
        // reply exists. Not a new turn end (live run 2026-11-07: "a duplicate step 2 report arrived").
        if (reply != null && reply == _lastReplyHandled) return;
        await AskAsync("worker_turn_ended: the worker finished a turn and is waiting at its prompt.", fromOwner: null, workerReply: reply ?? "");
    }

    private async Task<bool> SettleAsync()
    {
        var session = _session;
        while (true)
        {
            if (!_worker.IsRunning || _disposeCts.IsCancellationRequested || session != _session) return false;
            if (!_worker.IsAwaitingInput) return false;
            var silent = _worker.OutputSilentFor;
            if (silent >= TurnSettle) return true;
            var wait = TurnSettle - silent;
            try { await Task.Delay(wait < TimeSpan.FromMilliseconds(200) ? TimeSpan.FromMilliseconds(200) : wait, _disposeCts.Token); }
            catch (OperationCanceledException) { return false; }
        }
    }

    private async Task LoopAsync()
    {
        try
        {
            while (!_disposeCts.IsCancellationRequested)
            {
                await Task.Delay(LoopInterval, _disposeCts.Token);
                if (_restarting) continue;
                if (_worker.IsAtSecurityCheck) { NoteWaitingForOwner(); continue; }
                if (_held != null) { await DeliverHeldAsync(); continue; }
                // The terminal can miss a turn end (a message queued during a busy turn runs as the next one without the CLI
                // ever going quiet in between). The transcript records every turn end.
                if (!_thinking && (_sentSinceLastTurn || _handoffRequested) && _worker.IsRunning && _worker.ReadTurnEndedAt() is { } ended
                    && ended > _lastSubmitAt && DateTimeOffset.Now - ended >= TurnSettle && ended != _transcriptTurnHandled)
                {
                    _transcriptTurnHandled = ended;
                    await HandleTurnEndAsync(seenInTranscript: true);
                    continue;
                }
                if (_handoffRequested && DateTimeOffset.Now - _handoffRequestedAt > HandoffTimeout)
                {
                    AddExchange(ExchangeKind.Note, "The handoff turn took too long; starting the fresh session anyway.");
                    await PerformHandoffCycleAsync("the handoff turn timed out");
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AddExchange(ExchangeKind.Note, $"The orchestrator loop stopped: {ex.Message}"); }
    }

    // ---- Security checks are the owner's ----

    private bool _waitingNoted;

    /// <summary>Says once, per check, that the session is waiting for the owner's answer.</summary>
    private void NoteWaitingForOwner()
    {
        if (_waitingNoted) return;
        _waitingNoted = true;
        AddExchange(ExchangeKind.Note, "The session is showing a security check (folder trust or a permission). Answer it in the terminal; the orchestrator types nothing until you have.");
    }

    /// <summary>The check is gone: the message held for the session goes in now.</summary>
    private async Task DeliverHeldAsync()
    {
        await _gate.WaitAsync();
        try
        {
            _waitingNoted = false;
            if (_held is not { } text || _worker.IsAtSecurityCheck) return;
            _held = null;
            if (!_worker.IsRunning) { AddExchange(ExchangeKind.Note, "The session is not running, so the held message was not delivered."); return; }
            _lastSubmitAt = DateTimeOffset.Now;
            await _worker.SubmitAsync(text);
            _sentSinceLastTurn = true;
            _turnEndedWhileThinking = false;
            AddExchange(ExchangeKind.Sent, text);
        }
        finally { _gate.Release(); }
    }

    // ---- Handoff ----

    private void OnContextChanged(ContextReading? reading)
    {
        if (reading == null || _handoffRequested || _restarting || _disposeCts.IsCancellationRequested) return;
        if (reading.RecordedAt < _sessionStartedAt) return; // an earlier session's reading in the same pane
        var threshold = ContextThreshold;
        // The session's first reading is what the CLI already uses (system prompt, tools, CLAUDE.md). Over the limit already, a
        // handoff would only start another session over it, in a loop: give this session half the room it has left instead.
        if (_contextBaseline is not { } baseline)
        {
            _contextBaseline = baseline = reading.UsedFraction;
            if (baseline >= threshold)
                AddExchange(ExchangeKind.Note, $"This session already uses {baseline:P0} of its context window ({reading.UsedTokens:N0} of {reading.WindowTokens:N0} tokens), over the {threshold:P0} handoff limit; it hands off at {(baseline + 1) / 2:P0} instead.");
            return;
        }
        if (baseline >= threshold) threshold = (baseline + 1) / 2;
        if (reading.UsedFraction >= threshold)
            _ = RequestHandoffAsync($"context window at {reading.UsedFraction:P0} (limit {threshold:P0}, {reading.UsedTokens:N0} tokens)");
    }

    /// <summary>Asks the session to write its handoff now; the fresh session starts when this turn ends.</summary>
    public async Task RequestHandoffAsync(string reason)
    {
        await _gate.WaitAsync();
        try
        {
            if (_handoffRequested || _restarting || !_worker.IsRunning || _worker.SessionLaunch is null) return;
            if (_worker.IsAtSecurityCheck) { NoteWaitingForOwner(); return; } // asked again at the next context reading
            _handoffRequested = true;
            _handoffRequestedAt = DateTimeOffset.Now;
            _lastSubmitAt = DateTimeOffset.Now;
            AddExchange(ExchangeKind.Note, $"Handoff: {reason}. The session writes .agenthub/handoff.md, then a fresh session #{_session + 1} starts here.");
            AddExchange(ExchangeKind.AgentHub, OrchestratorMessages.HandoffPrompt);
            await _worker.SubmitAsync(OrchestratorMessages.HandoffPrompt);
        }
        finally { _gate.Release(); }
    }

    private async Task PerformHandoffCycleAsync(string why)
    {
        await _gate.WaitAsync();
        try
        {
            if (!_handoffRequested || _restarting || _disposeCts.IsCancellationRequested) return;
            var launch = _worker.SessionLaunch;
            if (launch is null) { _handoffRequested = false; return; }
            _restarting = true;
            _handoffRequested = false;
            Changed?.Invoke();

            AddExchange(ExchangeKind.Note, $"Session #{_session} ended ({why}); starting session #{_session + 1}: {launch.Value.CommandLine} in {launch.Value.WorkingDirectory}.");
            if (_worker.IsRunning)
            {
                _expectExit = true;
                _worker.Stop();
                await Task.Delay(StopDelay, _disposeCts.Token);
            }
            _session++;
            _sessionStartedAt = DateTimeOffset.Now;
            _contextBaseline = null;
            _lastReplyHandled = null;
            _sentSinceLastTurn = false;
            _turnEndedWhileThinking = false;
            _transcriptTurnHandled = null;
            _expectExit = false;
            _worker.Start(launch.Value.CommandLine, launch.Value.WorkingDirectory, launch.Value.Title, launch.Value.SessionName);
            await Task.Delay(StartupDelay, _disposeCts.Token);
        }
        catch (OperationCanceledException) { return; }
        finally
        {
            _restarting = false;
            _gate.Release();
        }
        Changed?.Invoke();

        var handoff = ReadHandoff();
        await AskAsync($"new_worker_session: session #{_session} is a fresh session with an empty memory, started after a handoff. It has not been given anything yet. " +
            "Write its first instruction (to_worker): read .agenthub/handoff.md first, then carry on with the job and the owner's standing instructions.",
            fromOwner: null, workerReply: null, handoff: handoff);
    }

    private string? ReadHandoff()
    {
        var dir = _worker.SessionLaunch?.WorkingDirectory ?? _projectDirectory;
        if (string.IsNullOrEmpty(dir)) return null;
        try
        {
            var path = Path.Combine(dir, ".agenthub", "handoff.md");
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private void OnWorkerExited()
    {
        if (_expectExit) { _expectExit = false; return; }
        if (_handoffRequested && !_restarting) _ = PerformHandoffCycleAsync("the session ended during its handoff turn");
    }

    // ---- One orchestrator turn ----

    private async Task AskAsync(string eventText, string? fromOwner, string? workerReply, string? handoff = null)
    {
        await _gate.WaitAsync();
        try
        {
            if (_disposeCts.IsCancellationRequested) return;
            if (_decisions >= MaxDecisions)
            {
                AddExchange(ExchangeKind.Note, $"The orchestrator reached its limit of {MaxDecisions} decisions. Detach and attach it again to carry on.");
                return;
            }

            string? soFar = null;
            if (!_introduced)
            {
                var prompt = _worker.ReadLastPrompt();
                var last = workerReply is null ? _worker.ReadLastReply() : null;
                if (prompt != null || last != null)
                    soFar = (prompt is { Length: > 0 } ? "Last prompt the worker got:\n" + OrchestratorMessages.Cut(prompt, 2000, keepTail: false) + "\n\n" : "")
                          + (last is { Length: > 0 } ? "Its last reply:\n" + OrchestratorMessages.Cut(last, OrchestratorMessages.MaxWorkerReplyChars, keepTail: true) : "");
            }
            var message = Compose(eventText, fromOwner, workerReply, soFar, Status(), handoff);
            if (workerReply is not null) AddExchange(ExchangeKind.Read, workerReply.Length == 0 ? "(no text reply)" : workerReply);

            SetThinking(true);
            PaneDecision d;
            try { d = await _brain.DecideAsync(message, _disposeCts.Token); }
            catch (OrchestratorBrainException ex)
            {
                AddExchange(ExchangeKind.Note, (ex.UsageLimit ? "Claude usage limit: " : "The orchestrator failed: ") + ex.Message);
                return;
            }
            catch (OperationCanceledException) { return; }
            finally { SetThinking(false); }

            _introduced = true;
            _decisions++;
            _orchestratorContext = _brain.ReadContext() ?? _orchestratorContext; // its transcript has this decision's usage
            if (workerReply is not null) { _lastReplyHandled = workerReply; _sentSinceLastTurn = false; _turnEndedWhileThinking = false; }
            if (d.Note.Length > 0) { Note = d.Note; Changed?.Invoke(); }

            if (d.ToOwner != null) AddExchange(ExchangeKind.Reply, d.ToOwner);
            var toWorker = d.ToWorker;
            if (toWorker == null && handoff != null)
                toWorker = "Read .agenthub/handoff.md and the code, then carry on with the job in it."; // a fresh session must be told something
            if (toWorker != null && _handoffRequested)
            {
                AddExchange(ExchangeKind.Note, "Held: the session is writing its handoff; the fresh session gets the next instruction.");
            }
            else if (toWorker != null && _worker.IsRunning && _worker.IsAtSecurityCheck)
            {
                _held = toWorker;
                AddExchange(ExchangeKind.Note, "Held: the session is showing a security check. Answer it in the terminal and this message goes in: " + OrchestratorMessages.Cut(toWorker, 200, keepTail: false));
            }
            else if (toWorker != null)
            {
                if (_worker.IsRunning)
                {
                    _lastSubmitAt = DateTimeOffset.Now;
                    await _worker.SubmitAsync(toWorker);
                    _sentSinceLastTurn = true;
                    _turnEndedWhileThinking = false;
                    AddExchange(ExchangeKind.Sent, toWorker);
                }
                else AddExchange(ExchangeKind.Note, "The session is not running, so the orchestrator's message was not delivered.");
            }
            else if (d.ToOwner == null) AddExchange(ExchangeKind.Wait, d.Note.Length > 0 ? d.Note : "nothing to send yet");
        }
        finally { _gate.Release(); }

        if (_turnEndedWhileThinking)
        {
            _turnEndedWhileThinking = false;
            _ = HandleTurnEndAsync();
        }
    }

    private string Status()
    {
        var parts = new List<string> { $"session {_session}", $"decision {_decisions + 1}", _worker.IsRunning ? (_worker.IsAwaitingInput ? "worker idle" : "worker busy") : "worker not running" };
        if (_worker.LatestContextReading is { } c) parts.Add($"worker context {c.UsedFraction:P0} of {c.WindowTokens / 1000:N0}k, handoff at {ContextThreshold:P0}");
        return string.Join(" · ", parts);
    }

    private void SetThinking(bool thinking)
    {
        _thinking = thinking;
        Changed?.Invoke();
    }

    private void AddExchange(ExchangeKind kind, string text)
    {
        lock (_conversationLock) _conversation = [.. _conversation, new OrchestratorExchange(DateTimeOffset.Now, kind, text)];
        Changed?.Invoke();
    }

    /// <summary>Detached: the session carries on as the owner's own; the orchestrator's Claude session is simply not asked again.</summary>
    public void Dispose()
    {
        _worker.TurnEnded -= OnWorkerTurnEnded;
        _worker.ContextChanged -= OnContextChanged;
        _worker.Exited -= OnWorkerExited;
        _disposeCts.Cancel();
        (_worker as IDisposable)?.Dispose();
    }
}
