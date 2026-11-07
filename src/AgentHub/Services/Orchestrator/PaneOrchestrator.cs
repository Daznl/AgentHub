using System.IO;
using System.Text;
using System.Text.Json;

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
}

/// <summary>The real brain: a <see cref="ClaudeJsonSession"/> answering with <see cref="PaneDecision"/>s.</summary>
public sealed class ClaudePaneBrain(ClaudeJsonSession session) : IPaneBrain
{
    public async Task<PaneDecision> DecideAsync(string message, CancellationToken cancellationToken) =>
        (await session.AskAsync(message, PaneDecision.Parse, cancellationToken)).Value;
}

/// <summary>
/// An orchestrator the owner puts on a running Cockpit session with one click, and takes off again as freely (owner feedback
/// 2026-11-07: the run orchestrator is "a bit too formal"; "I want to add orchestration freely onto a currently running
/// session ... all inputs just go to the orchestrator and the orchestrator reply"). Nothing is started or recorded: the
/// session already runs and stays the owner's. The orchestrator sits between them: every message the owner types into the
/// box goes to it, it replies to the owner and/or instructs the worker, and every worker turn end is shown to it too.
/// Its context is whatever the owner tells it (its standing instructions) plus, optionally, a workflow's instructions.
/// No handoffs, usage checks, stages or run archive: a thin loop on purpose.
/// </summary>
public sealed class PaneOrchestrator : IDisposable
{
    private readonly IBuildWorker _worker;
    private readonly IPaneBrain _brain;
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

    /// <summary>What the orchestrator is for, in the owner's words: shown in the box title.</summary>
    public string Title { get; }
    /// <summary>The workflow whose instructions were added to the orchestrator's context, if any.</summary>
    public OrchestratorWorkflow? Workflow { get; }
    public bool IsThinking => _thinking;
    public string Note { get; private set; } = "";
    public int Decisions => _decisions;
    /// <summary>The conversation so far, oldest first. Replaced, never changed in place, so any thread gets a whole list.</summary>
    public IReadOnlyList<OrchestratorExchange> Conversation => _conversation;

    /// <summary>Anything shown changed (thinking, note, conversation). From any thread.</summary>
    public event Action? Changed;

    /// <summary>A turn end counts once the worker has been quiet this long (it may print a little after its prompt returns).</summary>
    public TimeSpan TurnSettle { get; set; } = TimeSpan.FromSeconds(15);
    public TimeSpan LoopInterval { get; set; } = TimeSpan.FromSeconds(3);
    /// <summary>Upper bound on decisions, so a loop between the two sessions cannot run away.</summary>
    public int MaxDecisions { get; set; } = 300;

    public PaneOrchestrator(IBuildWorker worker, IPaneBrain brain, string title, OrchestratorWorkflow? workflow = null)
    {
        _worker = worker;
        _brain = brain;
        Title = title;
        Workflow = workflow;
        _worker.TurnEnded += OnWorkerTurnEnded;
        _ = LoopAsync();
    }

    /// <summary>The orchestrator CLI. AGENTHUB_ORCHESTRATOR_COMMAND replaces it, for UI tests that use a scripted orchestrator.</summary>
    public static string DefaultCommand =>
        Environment.GetEnvironmentVariable("AGENTHUB_ORCHESTRATOR_COMMAND") is { Length: > 0 } c ? c : "claude";

    /// <summary>Where the orchestrator's own Claude session runs (its transcript), apart from the project's sessions.</summary>
    public static string DefaultSessionsRoot =>
        Environment.GetEnvironmentVariable("AGENTHUB_PANE_ORCHESTRATORS_DIR") is { Length: > 0 } d ? d : @"C:\data\agenthub\pane-orchestrators";

    /// <summary>The real thing for a Cockpit pane: a Claude Code session on the owner's login reading the pane's folder.</summary>
    public static PaneOrchestrator ForPane(IBuildWorker worker, ProcessRunner runner, string command, string model, string projectDirectory,
        string paneLabel, OrchestratorWorkflow? workflow, string? sessionsRoot = null)
    {
        var folder = Path.Combine(sessionsRoot ?? DefaultSessionsRoot, $"{DateTimeOffset.Now:yyyyMMdd-HHmmss}-{RunRecorder.Slug(Path.GetFileName(projectDirectory.TrimEnd('\\', '/')))}");
        var session = new ClaudeJsonSession(runner, command, workflow?.Model ?? model, folder, projectDirectory, SystemPrompt(projectDirectory, paneLabel, workflow), PaneDecision.Schema);
        return new PaneOrchestrator(worker, new ClaudePaneBrain(session), workflow?.Name ?? "Orchestrator", workflow);
    }

    // ---- Prompts ----

    public static string SystemPrompt(string projectDirectory, string paneLabel, OrchestratorWorkflow? workflow)
    {
        var core = $"""
            You are the ORCHESTRATOR attached to a running Claude Code session, the WORKER, in {projectDirectory} (AgentHub pane "{paneLabel}").
            The owner, the person using AgentHub, has put you between themselves and the worker: everything they type comes to you,
            not the worker, and your replies are shown to them. You direct the worker; you never do the work yourself.

            How this works:
            - Every message comes from AgentHub, labelled: [EVENT] what happened, [FROM OWNER] what the owner wrote, [WORKER FINAL REPLY]
              the worker's last message, [WORKER SO FAR] where the session stood when you were attached, [STATUS] numbers.
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
    public static string Compose(string eventText, string? fromOwner, string? workerReply, string? soFar, string? status)
    {
        var sb = new StringBuilder();
        sb.Append("[EVENT] ").Append(eventText).Append("\n\n");
        if (!string.IsNullOrWhiteSpace(soFar)) sb.Append("[WORKER SO FAR]\n").Append(soFar.Trim()).Append("\n\n");
        if (!string.IsNullOrWhiteSpace(status)) sb.Append("[STATUS] ").Append(status.Trim()).Append("\n\n");
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
        var eventText = busy
            ? "owner_message: the owner wrote to you. The worker is mid-turn: a to_worker is typed in now and the worker reads it at its next step."
            : "owner_message: the owner wrote to you. The worker is idle at its prompt.";
        await AskAsync(eventText, fromOwner: text, workerReply: null);
    }

    // ---- Worker turn ends ----

    private void OnWorkerTurnEnded() => _ = HandleTurnEndAsync();

    private async Task HandleTurnEndAsync(bool seenInTranscript = false)
    {
        if (_disposeCts.IsCancellationRequested) return;
        if (_thinking) { _turnEndedWhileThinking = true; return; }
        if (!seenInTranscript)
        {
            if (!await SettleAsync()) return;
            _transcriptTurnHandled = _worker.ReadTurnEndedAt();
        }
        var reply = _worker.ReadLastReply();
        if (reply != null && reply == _lastReplyHandled && !_sentSinceLastTurn) return; // the same turn reported twice
        await AskAsync("worker_turn_ended: the worker finished a turn and is waiting at its prompt.", fromOwner: null, workerReply: reply ?? "");
    }

    private async Task<bool> SettleAsync()
    {
        while (true)
        {
            if (!_worker.IsRunning || _disposeCts.IsCancellationRequested) return false;
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
                // The terminal can miss a turn end (a message queued during a busy turn runs as the next one without the CLI
                // ever going quiet in between). The transcript records every turn end.
                if (!_thinking && _sentSinceLastTurn && _worker.IsRunning && _worker.ReadTurnEndedAt() is { } ended
                    && ended > _lastSubmitAt && DateTimeOffset.Now - ended >= TurnSettle && ended != _transcriptTurnHandled)
                {
                    _transcriptTurnHandled = ended;
                    await HandleTurnEndAsync(seenInTranscript: true);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AddExchange(ExchangeKind.Note, $"The orchestrator loop stopped: {ex.Message}"); }
    }

    // ---- One orchestrator turn ----

    private async Task AskAsync(string eventText, string? fromOwner, string? workerReply)
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
            var message = Compose(eventText, fromOwner, workerReply, soFar, Status());
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
            if (workerReply is not null) { _lastReplyHandled = workerReply; _sentSinceLastTurn = false; _turnEndedWhileThinking = false; }
            if (d.Note.Length > 0) { Note = d.Note; Changed?.Invoke(); }

            if (d.ToOwner != null) AddExchange(ExchangeKind.Reply, d.ToOwner);
            if (d.ToWorker != null)
            {
                if (_worker.IsRunning)
                {
                    _lastSubmitAt = DateTimeOffset.Now;
                    await _worker.SubmitAsync(d.ToWorker);
                    _sentSinceLastTurn = true;
                    _turnEndedWhileThinking = false;
                    AddExchange(ExchangeKind.Sent, d.ToWorker);
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
        var parts = new List<string> { $"decision {_decisions + 1}", _worker.IsRunning ? (_worker.IsAwaitingInput ? "worker idle" : "worker busy") : "worker not running" };
        if (_worker.LatestContextReading is { } c) parts.Add($"worker context {c.UsedFraction:P0} of {c.WindowTokens / 1000:N0}k");
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
        _disposeCts.Cancel();
        (_worker as IDisposable)?.Dispose();
    }
}
