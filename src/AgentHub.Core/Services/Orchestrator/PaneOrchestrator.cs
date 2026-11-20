using System.IO;
using System.Text;
using System.Text.Json;
using AgentHub.Services.Context;

namespace AgentHub.Services.Orchestrator;

/// <summary>
/// The pane orchestrator's one output per message: a word for the owner, a message for the worker, or both; rarely, a
/// request to hand the worker off to a fresh session now (<paramref name="NewSession"/>, the reason), before its context fills.
/// </summary>
/// <param name="Outcome">What it says about the current step of a run (<see cref="TemplateRun"/>); null without a run or when no step is concerned.</param>
public sealed record PaneDecision(string? ToOwner, string? ToWorker, string Note, StepOutcome? Outcome = null, string? NewSession = null)
{
    /// <summary>The reply of an orchestrator that follows an advanced template: <see cref="Schema"/> plus <c>step_outcome</c>.</summary>
    public const string RunSchema = """
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "to_owner": { "type": ["string", "null"], "description": "Your reply to the owner, shown in AgentHub. Null when there is nothing to tell them now." },
            "to_worker": { "type": ["string", "null"], "description": "The exact message to type into the worker session. Null to send it nothing now." },
            "note": { "type": "string", "description": "One line on where things stand, shown under the box title." },
            "step_outcome": { "type": ["string", "null"], "enum": ["done", "continuing", "escalate", null], "description": "The current step of the run: done, continuing or escalate. Null when no step is concerned." },
            "new_session": { "type": ["string", "null"], "description": "Null unless a fresh session is due under your rules (the owner asked, or new work on a context over the fresh-session mark): the reason to hand the worker off to a fresh session now." }
          },
          "required": ["to_owner", "to_worker", "note", "step_outcome", "new_session"]
        }
        """;

    public const string Schema = """
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "to_owner": { "type": ["string", "null"], "description": "Your reply to the owner, shown in AgentHub. Null when there is nothing to tell them now." },
            "to_worker": { "type": ["string", "null"], "description": "The exact message to type into the worker session. Null to send it nothing now." },
            "note": { "type": "string", "description": "One line on where things stand, shown under the box title." },
            "new_session": { "type": ["string", "null"], "description": "Null unless a fresh session is due under your rules (the owner asked, or new work on a context over the fresh-session mark): the reason to hand the worker off to a fresh session now." }
          },
          "required": ["to_owner", "to_worker", "note", "new_session"]
        }
        """;

    public static PaneDecision Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new FormatException("The reply must be a JSON object.");
        string? Str(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()?.Trim() : null;
        string? NonBlank(string name) => Str(name) is { Length: > 0 } s ? s : null;
        StepOutcome? outcome = Str("step_outcome")?.ToLowerInvariant() switch
        {
            null or "" => null,
            "done" => StepOutcome.Done,
            "continuing" => StepOutcome.Continuing,
            "escalate" => StepOutcome.Escalate,
            var other => throw new FormatException($"step_outcome \"{other}\" is not one of done, continuing, escalate or null."),
        };
        return new PaneDecision(NonBlank("to_owner"), NonBlank("to_worker"), Str("note") ?? "", outcome, NonBlank("new_session"));
    }
}

/// <summary>Answers one message from the pane orchestrator (tests script it).</summary>
public interface IPaneBrain
{
    Task<PaneDecision> DecideAsync(string message, CancellationToken cancellationToken);

    /// <summary>The brain's own context window use after its last decision; null when unknown (scripted brains).</summary>
    ContextReading? ReadContext() => null;

    /// <summary>The brain's own session, so a saved run can pick the same one up again; null when it has none (scripted brains).</summary>
    string? SessionId => null;
    /// <summary>The folder that session runs in.</summary>
    string? SessionFolder => null;
}

/// <summary>The real brain: a <see cref="ClaudeJsonSession"/> answering with <see cref="PaneDecision"/>s.</summary>
public sealed class ClaudePaneBrain(ClaudeJsonSession session, long assumedWindowTokens = 200_000) : IPaneBrain
{
    public string? SessionId => session.SessionId;
    public string? SessionFolder => session.WorkingDirectory;

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
/// briefs from the handoff. The orchestrator can start the same handoff early (<see cref="PaneDecision.NewSession"/>), which
/// its prompt calls for when the owner asked for it, or on its own when new work comes to a session whose context is over
/// <see cref="FreshSessionMark"/> (owner 2026-11-13: a review pass went into a session already at 187k tokens and hit the hard
/// limit mid-deploy; "it should typically choose to handoff and create a new session").
/// </summary>
public sealed partial class PaneOrchestrator : IDisposable
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
    private string? _lastReplyHandled;
    private DateTimeOffset _lastSubmitAt = DateTimeOffset.MinValue;
    private DateTimeOffset? _transcriptTurnHandled;
    private readonly DateTimeOffset _attachedAt = DateTimeOffset.Now;
    private DateTimeOffset _lastDecisionAt = DateTimeOffset.Now;
    private int _idleNudges;
    private int _decisions;

    // ---- Handoff: a fresh session when the context window fills ----
    private bool _handoffRequested;
    private DateTimeOffset _handoffRequestedAt;
    private bool _restarting;
    private bool _expectExit;
    private int _session = 1;
    private DateTimeOffset _sessionStartedAt = DateTimeOffset.MinValue;
    private double? _contextBaseline;
    /// <summary>The orchestrator's to_worker that came with its own new_session: held from the old session, handed back for the fresh one.</summary>
    private string? _carriedInstruction;
    /// <summary>The worker has ended a turn in this session that the orchestrator read: it may then ask for a fresh session.</summary>
    private bool _workedThisSession;

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
    /// <summary>
    /// A backstop for a turn end that no signal caught: when the worker has written nothing and the orchestrator has not been
    /// asked for this long, its last reply is read again, and if nothing new came the orchestrator is told the worker is idle.
    /// Each further nudge waits twice as long, at most <see cref="MaxIdleNudges"/> times until something happens.
    /// </summary>
    public TimeSpan IdleNudge { get; set; } = TimeSpan.FromMinutes(10);
    public int MaxIdleNudges { get; set; } = 3;
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
            AddExchange(ExchangeKind.Note, $"Handoff limit set to {Percent.Of(next)} of the context window.");
            OnContextChanged(_worker.LatestContextReading);
        }
    }
    private double _contextThreshold = 0.75;
    private bool _constructed;

    /// <summary>Context size (tokens) from which new work goes to a fresh session: this, or half the handoff limit if lower.</summary>
    public const long FreshSessionTokens = 150_000;

    /// <summary>The fresh-session mark for a reading: <see cref="FreshSessionTokens"/>, or half the handoff limit on a small window.</summary>
    public long FreshSessionMark(ContextReading c) => Math.Min(FreshSessionTokens, (long)(c.WindowTokens * ContextThreshold / 2));

    /// <summary>The current session's latest context reading, when it is over the fresh-session mark.</summary>
    private ContextReading? OverFreshSessionMark() =>
        _worker.LatestContextReading is { } c && c.RecordedAt >= _sessionStartedAt && c.UsedTokens >= FreshSessionMark(c) ? c : null;

    /// <summary>Added to an event when new work would now go to a fresh session, so the rule does not rest on reading [STATUS].</summary>
    private string FreshSessionHint()
    {
        if (OverFreshSessionMark() is not { } c || NewSessionRefusal() != null) return "";
        return $" The worker's context holds {c.UsedTokens:N0} tokens, over the {FreshSessionMark(c):N0}-token fresh-session mark: " +
            "if what comes next is new work rather than the task in progress, set new_session and put that work in to_worker.";
    }
    public const double MinContextThreshold = 0.40;
    public const double MaxContextThreshold = 0.95;

    /// <summary>The worker's latest context reading, for the box (null until the first one).</summary>
    public ContextReading? LatestContext => _worker.LatestContextReading;
    /// <summary>A handoff turn that takes longer than this is cut short and the fresh session started anyway.</summary>
    public TimeSpan HandoffTimeout { get; set; } = TimeSpan.FromMinutes(10);
    /// <summary>Time for a fresh CLI to show its prompt before its first instruction is typed, counted once its session runs.</summary>
    public TimeSpan StartupDelay { get; set; } = TimeSpan.FromSeconds(4);
    /// <summary>
    /// How long a started session may take to report running. A hosted session runs only once its host process has started
    /// and connected, which can take several seconds (Tuesday run 2026-11-18: longer than the old fixed wait, and the run
    /// paused at the next step for "no worker session").
    /// </summary>
    public TimeSpan SessionStartTimeout { get; set; } = TimeSpan.FromSeconds(90);
    /// <summary>Time for the old CLI to go after it was stopped.</summary>
    public TimeSpan StopDelay { get; set; } = TimeSpan.FromMilliseconds(2500);

    /// <param name="advanced">An advanced template to follow step by step (<see cref="Run"/>); the run waits for <see cref="GoAsync"/>.</param>
    /// <exception cref="InvalidOperationException"><paramref name="advanced"/> has problems and cannot be run.</exception>
    /// <param name="resume">A saved run of <paramref name="advanced"/> to pick up where it stood (<see cref="TemplateRun.Restore"/>); it comes back paused.</param>
    /// <param name="brainRemembers">The brain is the session that followed the run before, so it is not shown the worker's history again.</param>
    public PaneOrchestrator(IBuildWorker worker, IPaneBrain brain, string title, OrchestratorWorkflow? workflow = null, string? projectDirectory = null,
        AdvancedTemplate? advanced = null, RunSnapshot? resume = null, bool brainRemembers = false)
    {
        _worker = worker;
        _brain = brain;
        Title = title;
        Workflow = workflow;
        _projectDirectory = projectDirectory;
        if (advanced is not null)
        {
            Run = resume is null ? new TemplateRun(advanced) : TemplateRun.Restore(advanced, resume, "Picked up from a saved run. Press Go to carry on.");
            // A step that was under way is picked up, not started again: Go shows the orchestrator the worker's latest reply.
            if (resume is { State: not RunState.Ready }) _stepBegun = Run.Current?.Step.Id;
            _introduced = resume is not null && brainRemembers;
        }
        _launch = worker.SessionLaunch;
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
        string paneLabel, OrchestratorWorkflow? workflow, string? sessionsRoot = null, long assumedWindowTokens = 200_000, AdvancedTemplate? advanced = null,
        RunResume? resume = null)
    {
        var folder = Path.Combine(sessionsRoot ?? DefaultSessionsRoot, $"{DateTimeOffset.Now:yyyyMMdd-HHmmss}-{OrchestratorMessages.Slug(Path.GetFileName(projectDirectory.TrimEnd('\\', '/')))}");
        // Its own session loads no CLAUDE.md, so the worker's instruction files are copied into its prompt.
        var rules = OrchestratorRules.Gather(projectDirectory, Instructions.InstructionFiles.DetectCli(worker.SessionLaunch?.CommandLine));
        var prompt = SystemPrompt(projectDirectory, paneLabel, workflow, rules, advanced);
        var schema = advanced is null ? PaneDecision.Schema : PaneDecision.RunSchema;
        var brainModel = advanced?.Model ?? workflow?.Model ?? model;

        // A saved run goes on with the orchestrator session that followed it, which remembers the job and the owner's
        // instructions, as long as Claude Code still has its transcript; else with a new one, shown the worker's history.
        ClaudeJsonSession? session = null;
        if (resume is { BrainSessionId: { Length: > 0 } id, BrainFolder: { Length: > 0 } was })
        {
            var same = new ClaudeJsonSession(runner, command, brainModel, was, projectDirectory, prompt, schema, id, alreadyStarted: true);
            if (File.Exists(same.TranscriptPath)) session = same;
        }
        var remembers = session is not null;
        session ??= new ClaudeJsonSession(runner, command, brainModel, folder, projectDirectory, prompt, schema);
        return new PaneOrchestrator(worker, new ClaudePaneBrain(session, assumedWindowTokens), advanced?.Name ?? workflow?.Name ?? "Orchestrator", workflow, projectDirectory,
            advanced, resume?.Snapshot, remembers);
    }

    /// <summary>The orchestrator's own context window use, read from its transcript after each decision; null until it has decided once.</summary>
    public ContextReading? OrchestratorContext => _orchestratorContext;
    private ContextReading? _orchestratorContext;

    // ---- Prompts ----

    /// <param name="rules">The worker's instruction files (<see cref="OrchestratorRules.Gather"/>); the fixed rules are always included.</param>
    /// <param name="advanced">The advanced template the orchestrator follows: adds the run's rules and its outline.</param>
    public static string SystemPrompt(string projectDirectory, string paneLabel, OrchestratorWorkflow? workflow, IReadOnlyList<RulesFile>? rules = null, AdvancedTemplate? advanced = null)
    {
        var prompt = BasePrompt(projectDirectory, paneLabel, workflow, rules);
        return advanced is null ? prompt : prompt + "\n\n\n" + RunPrompt(advanced);
    }

    private static string BasePrompt(string projectDirectory, string paneLabel, OrchestratorWorkflow? workflow, IReadOnlyList<RulesFile>? rules)
    {
        var core = $"""
            You are the ORCHESTRATOR attached to a running Claude Code session, the WORKER, in {projectDirectory} (AgentHub pane "{paneLabel}").
            The owner, the person using AgentHub, has put you between themselves and the worker: everything they type comes to you,
            not the worker, and your replies are shown to them. You direct the worker; you never do the work yourself.

            How this works:
            - Every message comes from AgentHub, labelled: [EVENT] what happened, [FROM OWNER] what the owner wrote, [WORKER FINAL REPLY]
              the worker's last message, [CONVERSATION HISTORY] (your first message only) everything the session was sent before you
              were attached with the end of each reply, [HANDOFF.MD] the worker's notes file after a handoff, [STATUS] numbers.
            - [CONVERSATION HISTORY] is background, not a request: read it for the job, the owner's standing instructions and what is
              already done, then act on the [EVENT] of the message it came with. Do not redo or re-send work it shows finished.
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
            - worker_idle means the worker has been quiet at its prompt for a while and no turn end came since your last decision.
              If it is waiting on something it started (a deploy, a test run), leave it; if it has stopped and the owner's standing
              instructions call for a next step, give it; if it is the owner's call, ask them.
            - If the worker asks a question that the owner's instructions already answer, answer it yourself; otherwise put it to the owner.
            - Handoffs: when the worker's context window fills, AgentHub has it write .agenthub/handoff.md, ends that session and starts a
              fresh one with an empty memory in the same pane. You are then asked for the fresh session's first instruction: it must start
              by reading .agenthub/handoff.md, and you carry over the job and the owner's standing instructions in your own words.
            - new_session: the reason to start that handoff early, before the context fills; null otherwise. Set it:
              (a) when the owner asks for it, now or in their standing instructions (for example "a fresh session for each task") and
                  the moment they named has come; or
              (b) on your own, when the next thing to do (the owner's new message, or the next item of their standing instructions) is
                  new work, the worker's current task is finished, and [STATUS] shows the worker's context at or over the fresh-session
                  mark. New work means anything beyond the task in progress: a new feature, a new stage, a review or test pass over
                  finished work. A full context makes the worker slower, costlier and more error-prone, and new work would hit the hard
                  limit midway. Keep the session only when the message continues the very task in progress (an answer to its question,
                  a correction, a fix to what it just did), when the worker is mid-task, or when the owner said to stay in this session.
              Never twice in a row without new work in between, and never during an advanced-template run (it starts fresh sessions
              at its own steps). With new_session set, tell the owner why in to_owner and put the new task in to_worker as usual: the
              old session is only asked to write .agenthub/handoff.md, and AgentHub gives your instruction back to you when the fresh
              session starts, for its first instruction. Under the mark, keep the session; if a fresh one would still help, suggest it
              in to_owner.
            - You can read the project folder (Read, Grep, Glob) to check claims; keep that brief.

            {OrchestratorRules.HardRules}
            Your only output is the JSON: to_owner, to_worker, note (one line on where things stand), new_session (null unless above).
            """;
        var section = OrchestratorRules.Section(rules ?? []);
        if (section.Length > 0) core += "\n\n" + section;
        if (workflow == null) return core;
        return core + $"""


            ===== CONTEXT: {workflow.Name} =====
            {workflow.Description}

            {workflow.ResolvedInstructions()}
            """;
    }

    /// <summary>One message for the orchestrator.</summary>
    /// <param name="history">The session before the orchestrator was attached (<see cref="ConversationHistory.Format"/>); first message only.</param>
    /// <param name="run">Where the run of an advanced template stands (<see cref="TemplateRun.Describe"/>); null without one.</param>
    public static string Compose(string eventText, string? fromOwner, string? workerReply, string? history, string? status, string? handoff = null, string? run = null)
    {
        var sb = new StringBuilder();
        sb.Append("[EVENT] ").Append(eventText).Append("\n\n");
        if (!string.IsNullOrWhiteSpace(run)) sb.Append("[RUN] ").Append(run.Trim()).Append("\n\n");
        if (!string.IsNullOrWhiteSpace(history)) sb.Append("[CONVERSATION HISTORY]\n").Append(history.Trim()).Append("\n\n");
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
        var busy = _worker.IsRunning && !WorkerAtPrompt();
        var eventText = IsHandingOff
            ? "owner_message: the owner wrote to you. The worker is handing off (writing its notes before a fresh session starts): hold any instruction for the fresh session; answer the owner."
            : Run is { State: RunState.Paused, Cause: PauseCause.Question }
            ? "owner_message: the owner answered the question of the current step (see [RUN]). If that settles it: step_outcome \"done\". If you need more from them, ask in to_owner with step_outcome \"continuing\"."
            : Run is { State: RunState.Paused, Cause: PauseCause.Escalation }
            ? "owner_message: the run is paused on your escalation and the owner wrote back. If you now have what the step needs: step_outcome \"continuing\" (with to_worker when the worker needs telling) or \"done\"; the run then carries on. Still stuck: \"escalate\" again."
            : busy
            ? "owner_message: the owner wrote to you. The worker is mid-turn: a to_worker is typed in now and the worker reads it at its next step."
            : "owner_message: the owner wrote to you. The worker is idle at its prompt." + FreshSessionHint();
        await AskAndFollowAsync(eventText, fromOwner: text, workerReply: null);
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
        // A paused run leaves the worker alone: Go shows the orchestrator the worker's latest reply.
        if (Run is { State: RunState.Paused }) return;
        var reply = _worker.ReadLastReply();
        // The reply the orchestrator already read: the terminal went quiet (a long tool run, a dialog) before the worker's next
        // reply exists. Not a new turn end (live run 2026-11-07: "a duplicate step 2 report arrived").
        if (reply != null && reply == _lastReplyHandled) return;
        await AskAndFollowAsync(TurnEndedEvent + FreshSessionHint(), fromOwner: null, workerReply: reply ?? "");
    }

    private const string TurnEndedEvent = "worker_turn_ended: the worker finished a turn and is waiting at its prompt.";

    /// <summary>
    /// The worker is at its prompt. The terminal's flag stays set from one wait until someone types, so it misses a turn the
    /// worker started by itself (a background job finishing) and still reads "waiting" during that turn; the transcript
    /// records every turn end and every prompt (live run 2026-11-13: the box said "worker busy" for a worker idle 20 minutes).
    /// </summary>
    private bool WorkerAtPrompt()
    {
        if (_worker.ReadTurnEndedAt() is { } ended && ended > _sessionStartedAt) return _worker.OutputSilentFor >= TurnSettle;
        return _worker.IsAwaitingInput;
    }

    /// <summary>
    /// The worker has been silent and the orchestrator unasked for a while. A reply it has not read means a turn end was
    /// missed; otherwise it is told the worker is idle, so a run never just sits there until the owner writes.
    /// </summary>
    private async Task NudgeIfIdleAsync()
    {
        if (_thinking || _handoffRequested || !_introduced || !_worker.IsRunning || _idleNudges >= MaxIdleNudges) return;
        var wait = IdleNudge * (1 << _idleNudges);
        if (_worker.OutputSilentFor < wait || DateTimeOffset.Now - _lastDecisionAt < wait) return;
        var reply = _worker.ReadLastReply();
        if (reply != null && reply != _lastReplyHandled)
        {
            AddExchange(ExchangeKind.Note, "A worker turn end was missed; reading its reply now.");
            _transcriptTurnHandled = _worker.ReadTurnEndedAt();
            await AskAndFollowAsync(TurnEndedEvent, fromOwner: null, workerReply: reply);
            return;
        }
        _idleNudges++;
        await AskAndFollowAsync($"worker_idle: the worker has written nothing for {Minutes(_worker.OutputSilentFor)} and no turn end has come since your last decision; its last reply is the one you already read.",
            fromOwner: null, workerReply: null, nudge: true);
    }

    private static string Minutes(TimeSpan t) => t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes} min" : $"{(int)t.TotalSeconds} s";

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
                await WaitLoopIntervalAsync();
                if (_restarting) continue;
                if (_worker.IsAtSecurityCheck) { NoteWaitingForOwner(); continue; }
                if (_held != null) { await DeliverHeldAsync(); continue; }
                if (_awaitingQuiet && !_thinking) await FollowWhenQuietAsync();
                // The terminal can miss a turn end: a message queued during a busy turn runs as the next one without the CLI ever
                // going quiet in between, and a turn the worker starts by itself (a background job finishing) comes after a wait
                // no one typed into, so the terminal never re-arms (live run 2026-11-13: two finished parts sat unseen for 2 and
                // 21 minutes). The transcript records every turn end, whoever started the turn. Turns that ended before the
                // orchestrator was attached are the session's history, not news.
                if (!_thinking && _worker.IsRunning && _worker.ReadTurnEndedAt() is { } ended
                    && ended > _lastSubmitAt && ended > _attachedAt && ended > _sessionStartedAt
                    && DateTimeOffset.Now - ended >= TurnSettle && ended != _transcriptTurnHandled)
                {
                    _transcriptTurnHandled = ended;
                    await HandleTurnEndAsync(seenInTranscript: true);
                    continue;
                }
                await NudgeIfIdleAsync();
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

    /// <summary>
    /// Waits <see cref="LoopInterval"/>, read again every slice: the loop starts in the constructor, before an object initialiser
    /// sets the interval, so a single delay would always wait the default first.
    /// </summary>
    private async Task WaitLoopIntervalAsync()
    {
        var started = DateTimeOffset.Now;
        while (DateTimeOffset.Now - started < LoopInterval)
        {
            var left = LoopInterval - (DateTimeOffset.Now - started);
            await Task.Delay(left < LoopSlice ? (left > TimeSpan.Zero ? left : TimeSpan.FromMilliseconds(1)) : LoopSlice, _disposeCts.Token);
        }
    }

    private static readonly TimeSpan LoopSlice = TimeSpan.FromMilliseconds(250);

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
            _freshSession = false;
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
                AddExchange(ExchangeKind.Note, $"This session already uses {Percent.Of(baseline)} of its context window ({reading.UsedTokens:N0} of {reading.WindowTokens:N0} tokens), over the {Percent.Of(threshold)} handoff limit; it hands off at {Percent.Of((baseline + 1) / 2)} instead.");
            return;
        }
        if (baseline >= threshold) threshold = (baseline + 1) / 2;
        if (reading.UsedFraction >= threshold)
            _ = RequestHandoffAsync($"context window at {Percent.Of(reading.UsedFraction)} (limit {Percent.Of(threshold)},{reading.UsedTokens:N0} tokens)");
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
            if (!await RestartWorkerAsync(launch.Value))
            {
                AddExchange(ExchangeKind.Note, $"Session #{_session} did not start within {SessionStartTimeout.TotalSeconds:0} s. Start one in the pane; the orchestrator stays on.");
                return;
            }
        }
        catch (OperationCanceledException) { return; }
        finally
        {
            _restarting = false;
            _gate.Release();
        }
        Changed?.Invoke();

        var handoff = ReadHandoff();
        var carried = _carriedInstruction;
        _carriedInstruction = null;
        var carry = carried == null ? ""
            : " You started it for new work; the instruction you wrote for that work, not sent to the old session, was:\n\"\"\"\n" + carried +
              "\n\"\"\"\nGive that work to this session now, adapted as needed.";
        await AskAndFollowAsync($"new_worker_session: session #{_session} is a fresh session with an empty memory, started after a handoff. It has not been given anything yet. " +
            "Write its first instruction (to_worker): read .agenthub/handoff.md first, then carry on with the job and the owner's standing instructions." + carry,
            fromOwner: null, workerReply: null, handoff: handoff);
    }

    /// <summary>
    /// Why the orchestrator's own request for a fresh session cannot go ahead now; null when it can. A fresh session must have
    /// reported on some work first, so a confused orchestrator cannot start session after session.
    /// </summary>
    private string? NewSessionRefusal()
    {
        if (_handoffRequested || _restarting) return "a handoff is already under way";
        if (Run is { State: RunState.Running }) return "an advanced-template run is in progress, and it starts fresh sessions at its own steps";
        if (!_worker.IsRunning) return "the session is not running";
        if (_worker.SessionLaunch is null) return "AgentHub does not know how this session was started, so it cannot start it again";
        if (_session > 1 && !_workedThisSession) return $"session #{_session} has not finished any work yet";
        return null;
    }

    /// <summary>
    /// Ends the pane's session (when one runs) and starts a fresh one the same way, counted as the next session. The caller
    /// holds <see cref="_gate"/> and has set <see cref="_restarting"/>, so the session's end is not taken for the owner's.
    /// Returns once the new session runs and has had <see cref="StartupDelay"/> to show its prompt; false when it did not
    /// come up within <see cref="SessionStartTimeout"/>.
    /// </summary>
    private async Task<bool> RestartWorkerAsync(SessionLaunch launch)
    {
        if (_worker.IsRunning)
        {
            _expectExit = true;
            _worker.Stop();
            await Task.Delay(StopDelay, _disposeCts.Token);
        }
        _session++;
        _sessionStartedAt = DateTimeOffset.Now;
        _contextBaseline = null;
        _workedThisSession = false;
        _lastReplyHandled = null;
        _turnEndedWhileThinking = false;
        _transcriptTurnHandled = null;
        _expectExit = false;
        _freshSession = true;
        _worker.Start(launch.CommandLine, launch.WorkingDirectory, launch.Title, launch.SessionName);
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (!_worker.IsRunning && waited.Elapsed < SessionStartTimeout)
            await Task.Delay(SessionStartPoll, _disposeCts.Token);
        if (!_worker.IsRunning) return false;
        await Task.Delay(StartupDelay, _disposeCts.Token);
        return true;
    }

    private static readonly TimeSpan SessionStartPoll = TimeSpan.FromMilliseconds(100);

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
        if (_handoffRequested && !_restarting) { _ = PerformHandoffCycleAsync("the session ended during its handoff turn"); return; }
        if (!_restarting) OnWorkerEndedDuringRun();
    }

    // ---- One orchestrator turn ----

    /// <summary>One orchestrator turn, then on with the run when that turn finished its current step.</summary>
    private async Task AskAndFollowAsync(string eventText, string? fromOwner, string? workerReply, string? handoff = null, bool nudge = false)
    {
        if (await AskAsync(eventText, fromOwner, workerReply, handoff, nudge: nudge)) await FollowRunAsync();
    }

    /// <summary>Returns true when the decision finished the run's current step, so the caller moves on to the next one.</summary>
    /// <param name="fallbackToWorker">Typed into the worker when the orchestrator writes it nothing for a step that must tell it something.</param>
    /// <param name="nudge">An idle nudge: it does not reset the nudge count, so silence is not nudged about without end.</param>
    private async Task<bool> AskAsync(string eventText, string? fromOwner, string? workerReply, string? handoff = null, string? fallbackToWorker = null, bool nudge = false)
    {
        var advance = false;
        string? newSessionReason = null;
        await _gate.WaitAsync();
        try
        {
            if (_disposeCts.IsCancellationRequested) return false;
            // The terminal and the transcript can both report one turn end; the second waits here while the first is decided
            // (live run 2026-11-13: one turn end reached the orchestrator twice, 12 s apart).
            if (eventText.StartsWith(TurnEndedEvent, StringComparison.Ordinal) && workerReply is not null && workerReply == _lastReplyHandled) return false;
            if (!nudge) _idleNudges = 0;
            if (_decisions >= MaxDecisions)
            {
                AddExchange(ExchangeKind.Note, $"The orchestrator reached its limit of {MaxDecisions} decisions. Detach and attach it again to carry on.");
                Run?.Pause(PauseCause.Problem, $"The orchestrator reached its limit of {MaxDecisions} decisions.");
                return false;
            }
            var stepAtAsk = Run?.Current?.Step.Id;
            _awaitingQuiet = false; // the orchestrator is being brought in now, whatever the run was waiting for

            // The first message carries the whole session so far, so the orchestrator knows the job (a transcript can be tens of MB: read off this thread).
            string? history = null;
            if (!_introduced)
            {
                var entries = await Task.Run(_worker.ReadHistory);
                history = ConversationHistory.Format(entries, lastReplyFollows: workerReply is not null,
                    lastTurnRunning: _worker.IsRunning && !WorkerAtPrompt());
            }
            var planChanged = _planChanged;
            var message = Compose(eventText, fromOwner, workerReply, history, Status(), handoff, DescribeRun(planChanged));
            if (workerReply is not null) AddExchange(ExchangeKind.Read, workerReply.Length == 0 ? "(no text reply)" : workerReply);

            SetThinking(true);
            PaneDecision d;
            try { d = await _brain.DecideAsync(message, _disposeCts.Token); }
            catch (OrchestratorBrainException ex)
            {
                AddExchange(ExchangeKind.Note, (ex.UsageLimit ? "Claude usage limit: " : "The orchestrator failed: ") + ex.Message);
                if (Run is { State: RunState.Running } failed)
                {
                    failed.Pause(PauseCause.Problem, ex.UsageLimit ? "Claude usage limit reached." : "The orchestrator failed: " + ex.Message);
                    AddExchange(ExchangeKind.Note, "Run paused. Press Go to carry on from this step.");
                }
                return false;
            }
            catch (OperationCanceledException) { return false; }
            finally { SetThinking(false); _lastDecisionAt = DateTimeOffset.Now; }

            _introduced = true;
            if (planChanged) _planChanged = false; // it has seen the new outline
            _decisions++;
            _orchestratorContext = _brain.ReadContext() ?? _orchestratorContext; // its transcript has this decision's usage
            if (workerReply is not null) { _lastReplyHandled = workerReply; _turnEndedWhileThinking = false; _workedThisSession = true; }
            if (d.Note.Length > 0) { Note = d.Note; Changed?.Invoke(); }

            if (d.ToOwner != null) AddExchange(ExchangeKind.Reply, d.ToOwner);
            var toWorker = d.ToWorker;
            if (Run is { } run) toWorker = ApplyStepOutcome(run, d, stepAtAsk, fromOwner is not null, fallbackToWorker, out advance);
            if (d.NewSession is { } why)
            {
                if (NewSessionRefusal() is { } refusal)
                    AddExchange(ExchangeKind.Note, $"The orchestrator asked for a fresh session ({why}), but {refusal}.");
                else
                {
                    newSessionReason = why;
                    _carriedInstruction = toWorker; // the old session only gets the handoff request; this goes back to the orchestrator for the fresh one
                    toWorker = null;
                }
            }
            if (toWorker == null && handoff != null && newSessionReason == null && Run is not { State: RunState.Paused })
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
                    _freshSession = false;
                    _turnEndedWhileThinking = false;
                    AddExchange(ExchangeKind.Sent, toWorker);
                }
                else AddExchange(ExchangeKind.Note, "The session is not running, so the orchestrator's message was not delivered.");
            }
            else if (d.ToOwner == null && !advance && newSessionReason == null) AddExchange(ExchangeKind.Wait, d.Note.Length > 0 ? d.Note : "nothing to send yet");
        }
        finally { _gate.Release(); }

        if (newSessionReason != null)
        {
            _turnEndedWhileThinking = false; // a turn end from before the handoff request is not the handoff turn's end
            await RequestHandoffAsync($"the orchestrator started a fresh session early: {newSessionReason}");
            if (!_handoffRequested && !_restarting && _carriedInstruction is { } stranded)
            {
                // The handoff could not be asked for (a security check, the session gone): the new work must not vanish with it.
                _carriedInstruction = null;
                if (_worker.IsRunning && _worker.IsAtSecurityCheck)
                {
                    _held = stranded;
                    AddExchange(ExchangeKind.Note, "No fresh session: the session is showing a security check. Answer it and the orchestrator's instruction goes to this session.");
                }
                else AddExchange(ExchangeKind.Note, "No fresh session could start, so the orchestrator's instruction was not sent: " + OrchestratorMessages.Cut(stranded, 200, keepTail: false));
            }
            return false;
        }
        if (_turnEndedWhileThinking)
        {
            _turnEndedWhileThinking = false;
            _ = HandleTurnEndAsync();
        }
        return advance;
    }

    private string Status()
    {
        var parts = new List<string> { $"session {_session}", $"decision {_decisions + 1}", _worker.IsRunning ? (WorkerAtPrompt() ? "worker idle" : "worker busy") : "worker not running" };
        if (_worker.IsRunning && _freshSession) parts.Add("the worker session is fresh: empty memory, sent nothing yet");
        if (_worker.LatestContextReading is { } c)
            parts.Add($"worker context {c.UsedTokens / 1000:N0}k tokens ({Percent.Of(c.UsedFraction)} of {c.WindowTokens / 1000:N0}k), handoff at {Percent.Of(ContextThreshold)}, " +
                $"fresh session for new work from {FreshSessionMark(c) / 1000:N0}k");
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
