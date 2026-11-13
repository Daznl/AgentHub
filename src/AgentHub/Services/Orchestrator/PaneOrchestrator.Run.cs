using System.Text;

namespace AgentHub.Services.Orchestrator;

// Following an advanced template: the run. AgentHub keeps the place and moves it; the orchestrator words the worker's
// instructions and says, from the worker's replies, whether the current step is done, under way, or the owner's to settle.
public sealed partial class PaneOrchestrator
{
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private SessionLaunch? _launch;
    private bool _freshSession;
    private string? _stepBegun;

    /// <summary>The run of the advanced template this orchestrator follows; null for an orchestrator without one.</summary>
    public TemplateRun? Run { get; }

    /// <summary>How the pane's session was last started, as far as known: kept with a saved run.</summary>
    public SessionLaunch? Launch => _worker.SessionLaunch ?? _launch;
    /// <summary>The orchestrator's own Claude session and the folder it runs in; null for a scripted one. Kept with a saved run, to pick the same session up.</summary>
    public string? BrainSessionId => _brain.SessionId;
    public string? BrainFolder => _brain.SessionFolder;

    /// <summary>
    /// The pane's session has ended and the orchestrator stays on all the same: it is restarting the session itself, or it
    /// follows a run that is not finished (a close-session step ended it, or it ended by itself and the run paused).
    /// </summary>
    public bool KeepsEndedSession => _restarting || Run is { State: not RunState.Finished };

    /// <summary>
    /// ▶ Go: starts the run, carries a paused one on, or, with a step named, goes on from that step whatever the run was
    /// doing. Returns once the run is waiting again: for the worker, for the owner, or finished.
    /// </summary>
    /// <exception cref="InvalidOperationException">This orchestrator follows no advanced template.</exception>
    /// <exception cref="ArgumentException">No step has that id.</exception>
    public async Task GoAsync(string? fromStepId = null)
    {
        var run = Run ?? throw new InvalidOperationException("This orchestrator follows no advanced template.");
        if (fromStepId is null && run.State == RunState.Running) return;
        var was = run.State;
        run.Start(fromStepId);
        if (fromStepId is not null) _stepBegun = null; // the step starts afresh, even when the run was already at it
        AddExchange(ExchangeKind.Note, fromStepId is not null ? $"Run goes on from: {run.Current?.Step.DisplayTitle} ({run.Progress})."
            : was == RunState.Paused ? $"Run carries on ({run.Progress})."
            : $"Run started: {run.Template.Name}, {run.Steps.Count} steps.");
        await FollowRunAsync();
    }

    /// <summary>⏸ Pause: the run stops at its current step and the worker is sent nothing more until Go. The worker's own turn runs on.</summary>
    public void PauseRun()
    {
        if (Run is not { State: RunState.Running } run) return;
        run.Pause(PauseCause.Owner, "Paused by you.");
        AddExchange(ExchangeKind.Note, $"Run paused at {run.Progress}. The worker is sent nothing until you press Go.");
    }

    /// <summary>
    /// Carries the run on from its current step until it has to wait: AgentHub's own steps (new session, close session,
    /// ask the owner) are done here, the others are put to the orchestrator.
    /// </summary>
    private async Task FollowRunAsync()
    {
        if (Run is not { } run) return;
        await _runGate.WaitAsync();
        try
        {
            while (!_disposeCts.IsCancellationRequested && run.State == RunState.Running && run.Current is { } at)
            {
                var step = at.Step;
                if (_worker.SessionLaunch is { } launch) _launch = launch;
                var resumed = _stepBegun == step.Id;
                _stepBegun = step.Id;

                switch (step.Kind)
                {
                    case StepKind.NewSession:
                        if (_launch is not { } start)
                        {
                            PauseForProblem(run, "AgentHub does not know how this pane's session was started, so it cannot start a new one. Start one in the pane, then go on from the next step.");
                            return;
                        }
                        if (!await RestartForRunAsync(start)) return;
                        CompleteStep(run, at);
                        continue;

                    case StepKind.CloseSession:
                        await CloseForRunAsync();
                        CompleteStep(run, at);
                        continue;

                    case StepKind.AskOwner:
                        run.Pause(PauseCause.Question, step.Text);
                        AddExchange(ExchangeKind.Question, step.Text?.Trim() ?? "");
                        return;
                }

                if (!_worker.IsRunning)
                {
                    PauseForProblem(run, "No worker session is running in this pane. Start one, then press Go.");
                    return;
                }
                var waits = step.Kind == StepKind.WaitForWorker;
                // Mid-turn: not at its prompt, and printing. A session that has been quiet as long as a turn end takes to settle
                // is idle even when it was never seen to finish a turn (one just started, or a run picked up on it): waiting for
                // its turn end would wait for ever (UI test 2026-11-13: a run resumed on a fresh session never went on).
                var busy = !_worker.IsAwaitingInput && _worker.OutputSilentFor < TurnSettle;
                // A step the worker is still on (a wait, or a step picked up again): its turn end brings the orchestrator in, or,
                // when it was only starting up and no turn end follows, the loop does once it has gone quiet.
                if (busy && (waits || resumed))
                {
                    _awaitingQuiet = true;
                    return;
                }

                var reply = waits || resumed ? _worker.ReadLastReply() ?? "" : null;
                var eventText = resumed
                    ? "run_resumed: the run was paused at the current step (see [RUN]) and the owner pressed Go. The step may be partly or wholly done already: judge from the worker's latest reply. Done: step_outcome \"done\". Otherwise send what is still needed (to_worker) with \"continuing\"."
                    : waits
                    ? "step_started: the run has reached a wait-for-worker step (see [RUN]). Judge from the worker's latest reply whether its work is finished: \"done\", \"continuing\" or \"escalate\"."
                    : "step_started: the run has reached the step in [RUN]. Carry it out now: write the worker's instruction for it in to_worker, with step_outcome \"continuing\"." +
                      (busy ? " The worker is mid-turn: the instruction is typed in now and it reads it at its next step." : "");
                var fallback = waits || resumed ? null : TemplateRun.WorkerInstruction(step);
                if (!await AskAsync(eventText, fromOwner: null, workerReply: reply, fallbackToWorker: fallback)) return;
            }
        }
        finally { _runGate.Release(); }
    }

    /// <summary>The run left a step to the worker's turn end; set until the orchestrator is next brought in.</summary>
    private volatile bool _awaitingQuiet;

    /// <summary>
    /// The run is waiting for a turn end of a session that was printing when it looked (it was starting up, say) and may
    /// never end a turn: once the session has gone quiet, the run goes on without one. Called from the loop.
    /// </summary>
    private async Task FollowWhenQuietAsync()
    {
        if (Run is not { State: RunState.Running }) { _awaitingQuiet = false; return; }
        if (!_worker.IsRunning || _worker.IsAtSecurityCheck) return;
        if (!_worker.IsAwaitingInput && _worker.OutputSilentFor < TurnSettle) return;
        _awaitingQuiet = false;
        await FollowRunAsync();
    }

    /// <summary>
    /// Carries out what the orchestrator said about the run's current step, and returns what is to be typed into the
    /// worker (null for nothing). <paramref name="advance"/>: the step is done and the run has moved to the next one.
    /// </summary>
    private string? ApplyStepOutcome(TemplateRun run, PaneDecision d, string? stepAtAsk, bool fromOwner, string? fallbackToWorker, out bool advance)
    {
        advance = false;
        var toWorker = d.ToWorker;
        if (run.State is RunState.Ready or RunState.Finished) return toWorker; // no step is being followed: an orchestrator like any other

        string? NotSent(string why)
        {
            if (toWorker is not null) AddExchange(ExchangeKind.Note, $"Not sent to the worker ({why}): {OrchestratorMessages.Cut(toWorker, 200, keepTail: false)}");
            return null;
        }

        if (run.Current is not { } at || at.Step.Id != stepAtAsk) return NotSent("the run moved to another step while the orchestrator was deciding");

        if (run.State == RunState.Paused)
        {
            // The owner's answer to an escalation or to a step's question carries the run on; nothing else does but Go.
            var answered = fromOwner && run.Cause switch
            {
                PauseCause.Escalation => d.Outcome is StepOutcome.Done or StepOutcome.Continuing,
                PauseCause.Question => d.Outcome is StepOutcome.Done,
                _ => false,
            };
            if (!answered) return NotSent("the run is paused");
            run.Resume();
            AddExchange(ExchangeKind.Note, "Run carries on.");
        }

        switch (d.Outcome)
        {
            case StepOutcome.Done:
                NotSent("the step is done; the next step gives the worker its instruction");
                CompleteStep(run, at);
                advance = true;
                return null;

            case StepOutcome.Escalate:
                run.Pause(PauseCause.Escalation, d.ToOwner ?? d.Note);
                AddExchange(ExchangeKind.Note, $"Run paused at {run.Progress}: the orchestrator needs you. Answer it here and the run carries on once it has what it needs, or press Go.");
                return NotSent("the run is paused for you");

            default:
                if (toWorker is null && fallbackToWorker is not null)
                {
                    AddExchange(ExchangeKind.Note, "The orchestrator wrote no instruction for this step; AgentHub sends the step's own.");
                    return fallbackToWorker;
                }
                return toWorker;
        }
    }

    private void CompleteStep(TemplateRun run, RunStep step)
    {
        run.CompleteCurrent();
        AddExchange(ExchangeKind.Note, "✓ " + step.Step.DisplayTitle);
        if (run.State == RunState.Finished) AddExchange(ExchangeKind.Note, $"Run finished: all {run.Steps.Count} steps of {run.Template.Name} are done.");
    }

    private void PauseForProblem(TemplateRun run, string problem)
    {
        run.Pause(PauseCause.Problem, problem);
        AddExchange(ExchangeKind.Note, $"Run paused at {run.Progress}. {problem}");
    }

    /// <summary>A new-session step: a fresh session in the pane, started as the last one was. False when the orchestrator was detached meanwhile.</summary>
    private async Task<bool> RestartForRunAsync(SessionLaunch launch)
    {
        await _gate.WaitAsync();
        try
        {
            if (_disposeCts.IsCancellationRequested) return false;
            _restarting = true;
            _handoffRequested = false;
            Changed?.Invoke();
            AddExchange(ExchangeKind.Note, $"Starting session #{_session + 1}: {launch.CommandLine} in {launch.WorkingDirectory}.");
            await RestartWorkerAsync(launch);
            return true;
        }
        catch (OperationCanceledException) { return false; }
        finally
        {
            _restarting = false;
            _gate.Release();
            Changed?.Invoke();
        }
    }

    /// <summary>A close-session step: the pane's session is ended; the orchestrator and its run stay.</summary>
    private async Task CloseForRunAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (!_worker.IsRunning) return;
            AddExchange(ExchangeKind.Note, $"Closing session #{_session}.");
            _expectExit = true;
            _worker.Stop();
            // _expectExit is cleared by the session's end itself (OnWorkerExited), which may come after this wait.
            try { await Task.Delay(StopDelay, _disposeCts.Token); }
            catch (OperationCanceledException) { }
        }
        finally { _gate.Release(); }
    }

    /// <summary>The session ended and nothing of AgentHub's ended it: a running run cannot go on without it.</summary>
    private void OnWorkerEndedDuringRun()
    {
        if (Run is { State: RunState.Running } run) PauseForProblem(run, "The worker session ended. Start one in the pane, then press Go.");
    }

    /// <summary>The part of the system prompt an orchestrator that follows an advanced template gets: the run's rules and the template's outline.</summary>
    private static string RunPrompt(AdvancedTemplate template)
    {
        var sb = new StringBuilder();
        sb.Append($$"""
            ===== RUN: YOU FOLLOW AN ADVANCED TEMPLATE =====
            The owner has laid out the work as a fixed sequence of stages and steps: the advanced template "{{template.Name}}", outlined below.
            AgentHub keeps the place in it. Once the owner presses Go, every message has a [RUN] section naming the CURRENT STEP.
            - Work on the current step only. Never start a later step, skip one or change the order, however obvious the next move looks.
            - [RUN] is the truth, not the outline below: the owner can change the plan, pause the run, or send it on from another step.
            - Your JSON has a fourth field, step_outcome, your word on the current step:
              "done": the step is complete, judged from the worker's reply (or the owner's answer). AgentHub moves to the next step and asks
              you again, so leave to_worker null: an instruction sent with "done" is dropped.
              "continuing": the step is under way. Send to_worker what it needs (its instruction, a correction, an answer the owner's
              instructions already give), or null to keep waiting.
              "escalate": something unexpected happened, the worker asks what the owner's instructions do not answer, or the step cannot
              be done as written. Say what and why in to_owner. The run pauses until the owner answers; to_worker is not sent.
              null: no step is concerned (the run has not started, is paused by the owner, or is finished).
            - Do not call a step done on the worker's say-so alone when a quick look can check it (the branch exists, the file was written).
            - The steps, by kind:
              instruct: on step_started, send the step's text to the worker as its instruction, adding only what it needs to carry it out
              (it cannot see the template, and a fresh session remembers nothing). "done" once its reply shows it did it.
              create-branch, commit, merge-branch, move-work-to-branch, open-pull-request, handoff: the same; [RUN] says what the worker
              is to be told, and you word it. The worker does all git work; a step of the template is the owner asking for exactly
              that action, so it needs no further approval, but the rules you never break still hold: no force push, no rewriting
              pushed history, nothing destructive. If a step cannot be done within them: "escalate".
              wait-for-worker: nothing to send. From the worker's latest reply: its work is finished, "done"; it is still at it or you
              can answer it yourself, "continuing"; anything else, "escalate".
              ask-owner, new-session, close-session: AgentHub carries these out. For ask-owner you are shown the owner's answer:
              "done" when it settles the question.
            - At a step marked ESCALATION POINT, "escalate" whenever you are in doubt.
            - Keep the owner posted in to_owner at the turns that matter (a stage finished, something odd), in a line; not at every step.

            The template as it stood when you were attached:

            """);
        foreach (var stage in template.ExecutionOrder())
        {
            sb.Append($"Stage \"{stage.DisplayTitle}\"\n");
            foreach (var step in stage.Steps)
                sb.Append($"  - [{AdvancedTemplate.KindName(step.Kind)}] {step.DisplayTitle}{(step.Escalate ? " (escalation point)" : "")}\n");
        }
        return sb.ToString().TrimEnd();
    }
}
