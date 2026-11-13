using System.Text;

namespace AgentHub.Services.Orchestrator;

/// <summary>How far a run has got with one step.</summary>
public enum StepState { Pending, Running, Done, Skipped }

public enum RunState
{
    /// <summary>Attached, not started: the owner has not pressed Go.</summary>
    Ready,
    Running,
    /// <summary>Stopped at the current step; <see cref="TemplateRun.Cause"/> says why.</summary>
    Paused,
    /// <summary>Every step is done or skipped.</summary>
    Finished,
}

public enum PauseCause
{
    /// <summary>The owner pressed Pause.</summary>
    Owner,
    /// <summary>The orchestrator met something it cannot settle from the owner's instructions.</summary>
    Escalation,
    /// <summary>An ask-owner step put its question.</summary>
    Question,
    /// <summary>The run cannot go on as things are (no worker session).</summary>
    Problem,
}

/// <summary>What the orchestrator says about the current step in its reply (<c>step_outcome</c>).</summary>
public enum StepOutcome { Done, Continuing, Escalate }

/// <summary>Where a run stands, as plain values: what is saved, and what a saved run is restored from.</summary>
/// <param name="CurrentStepId">The step the run is at; null before the first Go and once finished.</param>
/// <param name="Steps">Each step's state by its id.</param>
public sealed record RunSnapshot(RunState State, PauseCause? Cause, string? Reason, string? CurrentStepId, IReadOnlyDictionary<string, StepState> Steps);

/// <summary>A step with the stage it belongs to.</summary>
public sealed record RunStep(TemplateStage Stage, TemplateStep Step);

/// <summary>
/// A run of an advanced template on one worker: the steps in the order a single worker follows
/// (<see cref="AdvancedTemplate.ExecutionOrder"/>), each step's state, and the place the run has reached. AgentHub keeps the
/// place, never the orchestrator: it only says whether the current step is done. No clock, files or sessions in here, so
/// the whole of it can be tested, and later saved, as plain state. Safe to read from any thread.
/// </summary>
public sealed class TemplateRun
{
    private readonly object _lock = new();
    private readonly StepState[] _states;
    private int _index = -1;
    private RunState _state = RunState.Ready;
    private PauseCause? _cause;
    private string? _reason;

    public AdvancedTemplate Template { get; }
    public IReadOnlyList<RunStep> Steps { get; }

    /// <exception cref="InvalidOperationException">The template has problems (<see cref="AdvancedTemplate.Validate"/>): it cannot be run.</exception>
    public TemplateRun(AdvancedTemplate template)
    {
        var problems = template.Validate();
        if (problems.Count > 0) throw new InvalidOperationException("This template cannot be run: " + string.Join(" ", problems));
        Template = template;
        Steps = template.ExecutionOrder().SelectMany(stage => stage.Steps.Select(step => new RunStep(stage, step))).ToList();
        _states = new StepState[Steps.Count];
    }

    public RunState State { get { lock (_lock) return _state; } }
    /// <summary>Why the run is paused; null unless it is.</summary>
    public PauseCause? Cause { get { lock (_lock) return _cause; } }
    /// <summary>The pause in words (the escalation, the question); null unless paused.</summary>
    public string? PauseReason { get { lock (_lock) return _reason; } }
    /// <summary>The step the run is at; null before the first Go and once finished.</summary>
    public RunStep? Current { get { lock (_lock) return _index >= 0 && _index < Steps.Count ? Steps[_index] : null; } }
    /// <summary>The current step's place, from 0; -1 when there is none.</summary>
    public int CurrentIndex { get { lock (_lock) return _index >= 0 && _index < Steps.Count ? _index : -1; } }
    public int DoneCount { get { lock (_lock) return _states.Count(s => s == StepState.Done); } }

    public StepState StateOf(string stepId)
    {
        lock (_lock)
        {
            var i = IndexOf(stepId);
            return i < 0 ? StepState.Pending : _states[i];
        }
    }

    private int IndexOf(string stepId)
    {
        for (var i = 0; i < Steps.Count; i++)
            if (Steps[i].Step.Id == stepId) return i;
        return -1;
    }

    /// <summary>
    /// Go. With no step named: start a ready run at its first step, carry a paused one on where it stands, start a
    /// finished one over. With a step named: go on from that step, whatever the run was doing. Steps before it that are
    /// not done count as skipped, and it and every step after it are to do (again).
    /// </summary>
    /// <exception cref="ArgumentException">No step has that id.</exception>
    public void Start(string? fromStepId = null)
    {
        lock (_lock)
        {
            if (fromStepId is null)
            {
                if (_state == RunState.Running) return;
                if (_state == RunState.Paused) { Run(); return; }
                Array.Fill(_states, StepState.Pending);
                _index = 0;
            }
            else
            {
                var from = IndexOf(fromStepId);
                if (from < 0) throw new ArgumentException($"The template has no step \"{fromStepId}\".", nameof(fromStepId));
                for (var i = 0; i < Steps.Count; i++)
                    _states[i] = i >= from ? StepState.Pending : _states[i] == StepState.Done ? StepState.Done : StepState.Skipped;
                _index = from;
            }
            Run();
        }
    }

    private void Run()
    {
        _state = RunState.Running;
        _cause = null;
        _reason = null;
        _states[_index] = StepState.Running;
    }

    /// <summary>Stops a running run at its current step, which stays the current one.</summary>
    public void Pause(PauseCause cause, string? reason)
    {
        lock (_lock)
        {
            if (_state != RunState.Running) return;
            _state = RunState.Paused;
            _cause = cause;
            _reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        }
    }

    /// <summary>Carries a paused run on at its current step.</summary>
    public void Resume()
    {
        lock (_lock)
        {
            if (_state == RunState.Paused) Run();
        }
    }

    /// <summary>The current step is done: the run moves to the next one, or is finished. Returns the new current step.</summary>
    public RunStep? CompleteCurrent()
    {
        lock (_lock)
        {
            if (_state != RunState.Running) return Current;
            _states[_index] = StepState.Done;
            _index++;
            if (_index >= Steps.Count)
            {
                _state = RunState.Finished;
                return null;
            }
            _states[_index] = StepState.Running;
            return Steps[_index];
        }
    }

    // ---- Kept on disk ----

    /// <summary>Where the run stands, as plain values to save (<see cref="RunStore"/>).</summary>
    public RunSnapshot Snapshot()
    {
        lock (_lock)
        {
            var steps = new Dictionary<string, StepState>(Steps.Count);
            for (var i = 0; i < Steps.Count; i++) steps[Steps[i].Step.Id] = _states[i];
            return new RunSnapshot(_state, _cause, _reason, _index >= 0 && _index < Steps.Count ? Steps[_index].Step.Id : null, steps);
        }
    }

    /// <summary>
    /// A saved run picked up again. It never comes back running: a run that was under way is paused at its step (the owner
    /// presses Go), and a pause it was saved with (a question, an escalation) is kept. Steps are matched by id, so a
    /// template changed in between keeps what was done: steps it no longer has are forgotten, new ones are to do.
    /// </summary>
    /// <param name="interrupted">The pause reason for a run that was running when it was saved.</param>
    /// <exception cref="InvalidOperationException">The template has problems: it cannot be run.</exception>
    public static TemplateRun Restore(AdvancedTemplate template, RunSnapshot snapshot, string interrupted)
    {
        var run = new TemplateRun(template);
        if (snapshot.State == RunState.Ready) return run;
        for (var i = 0; i < run.Steps.Count; i++)
        {
            var saved = snapshot.Steps.TryGetValue(run.Steps[i].Step.Id, out var s) ? s : StepState.Pending;
            run._states[i] = saved == StepState.Running ? StepState.Pending : saved;
        }
        // The step it was at; when the template no longer has it, or it was finished and has steps since: the first one still to do.
        var at = snapshot.CurrentStepId is { } id ? run.IndexOf(id) : -1;
        if (at < 0) at = Array.FindIndex(run._states, s => s == StepState.Pending);
        if (at < 0)
        {
            run._index = run.Steps.Count;
            run._state = RunState.Finished;
            return run;
        }
        run._index = at;
        run._states[at] = StepState.Running;
        run._state = RunState.Paused;
        var wasPaused = snapshot.State == RunState.Paused && snapshot.Cause is not null;
        run._cause = wasPaused ? snapshot.Cause : PauseCause.Owner;
        run._reason = wasPaused ? snapshot.Reason : interrupted;
        return run;
    }

    /// <summary>"step 3 of 15 · Stage 1 · Build stage 1", or where the run stands when it is at no step.</summary>
    public string Progress
    {
        get
        {
            lock (_lock)
            {
                if (_state == RunState.Finished) return $"all {Steps.Count} steps done";
                if (_index < 0) return $"{Steps.Count} steps, not started";
                var at = Steps[_index];
                return $"step {_index + 1} of {Steps.Count} · {at.Stage.DisplayTitle} · {at.Step.DisplayTitle}";
            }
        }
    }

    /// <summary>
    /// What a step asks of the worker, in plain words: the instruction AgentHub sends itself when the orchestrator writes
    /// none, and the meaning of the step the orchestrator is given. Null for the steps AgentHub carries out or that send
    /// the worker nothing (new-session, close-session, ask-owner, wait-for-worker).
    /// </summary>
    public static string? WorkerInstruction(TemplateStep step)
    {
        var text = string.IsNullOrWhiteSpace(step.Text) ? null : step.Text.Trim();
        var core = step.Kind switch
        {
            StepKind.Instruct => text,
            StepKind.Handoff => "Write .agenthub/handoff.md so that a fresh session with no memory can continue: what was built, the test status, decisions made and the next tasks in order. Update the project's documents that this work changed. Then end your turn.",
            StepKind.CreateBranch => $"Create the branch {step.Branch} from the current state and switch to it.",
            StepKind.Commit => "Commit your work to the current branch with a clear message.",
            StepKind.MergeBranch => $"Merge the branch {step.Branch} into {step.Target}. No force and no history rewrite; if there are conflicts, stop and report them.",
            StepKind.MoveWorkToBranch => $"Move your uncommitted work onto the branch {step.Branch} (create it if it does not exist) without losing any of it.",
            StepKind.OpenPullRequest => $"Push the current branch and open a pull request into {(string.IsNullOrWhiteSpace(step.Target) ? "the trunk" : step.Target)}. Report its link.",
            _ => null,
        };
        if (core is null) return null;
        return step.Kind == StepKind.Instruct || text is null ? core : core + " " + text;
    }

    /// <summary>The [RUN] section of every message to the orchestrator: where the run stands and what the current step asks.</summary>
    public string Describe()
    {
        lock (_lock)
        {
            var sb = new StringBuilder();
            sb.Append($"Advanced template \"{Template.Name}\". ");
            switch (_state)
            {
                case RunState.Ready:
                    return sb.Append($"The run has not started ({Steps.Count} steps): the owner has not pressed Go. Follow no step yet; step_outcome null.").ToString();
                case RunState.Finished:
                    return sb.Append($"The run is finished: all {Steps.Count} steps are done. step_outcome null.").ToString();
            }

            var at = Steps[_index];
            var stages = Steps.Select(s => s.Stage).Distinct().ToList();
            sb.Append($"Step {_index + 1} of {Steps.Count}, in stage \"{at.Stage.DisplayTitle}\" ({stages.IndexOf(at.Stage) + 1} of {stages.Count}). ");
            sb.Append(_state == RunState.Running ? "The run is running." : _cause switch
            {
                PauseCause.Owner => "The run is PAUSED by the owner: send the worker nothing and move no step until they press Go; step_outcome null.",
                PauseCause.Escalation => "The run is PAUSED on your escalation, waiting for the owner.",
                PauseCause.Question => "The run is PAUSED on this step's question, waiting for the owner's answer.",
                _ => "The run is PAUSED: " + (_reason ?? "it cannot go on as things are") + " step_outcome null.",
            });
            sb.Append('\n').Append("CURRENT STEP: ").Append(DescribeStep(at.Step));
            if (_index + 1 < Steps.Count) sb.Append('\n').Append("The step after it (not yet): ").Append(AdvancedTemplate.KindName(Steps[_index + 1].Step.Kind)).Append(", ").Append(Steps[_index + 1].Step.DisplayTitle);
            else sb.Append('\n').Append("It is the last step.");
            return sb.ToString();
        }
    }

    private static string DescribeStep(TemplateStep step)
    {
        var sb = new StringBuilder();
        sb.Append($"id {step.Id}, kind {AdvancedTemplate.KindName(step.Kind)}: {step.DisplayTitle}");
        var means = step.Kind switch
        {
            StepKind.WaitForWorker => "Nothing is sent for this step. Judge from the worker's latest reply whether the work of the steps before it is finished." +
                                      (string.IsNullOrWhiteSpace(step.Text) ? "" : " " + step.Text.Trim()),
            StepKind.AskOwner => "AgentHub has put this question to the owner: " + step.Text?.Trim(),
            StepKind.NewSession or StepKind.CloseSession => "AgentHub carries this out itself.",
            _ => "The worker is to be told: " + WorkerInstruction(step),
        };
        sb.Append('\n').Append("  ").Append(means);
        if (step.Escalate) sb.Append('\n').Append("  ESCALATION POINT: anything here the owner's instructions do not settle is \"escalate\", not your own call.");
        return sb.ToString();
    }
}
