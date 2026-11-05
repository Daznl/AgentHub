using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using AgentHub.Models;
using AgentHub.Services.Context;
using AgentHub.Services.Ideas;
using AgentHub.Services.Usage;
using AgentHub.Terminal;

namespace AgentHub.Services.Orchestrator;

public enum OrchestratorState
{
    Idle,
    Building,
    HandoffPending,
    WaitingForReset,
    Paused
}

public sealed record OrchestratorLog(
    DateTimeOffset Timestamp,
    string Message,
    string Level = "Info")
{
    public string FormattedTime => Timestamp.ToString("HH:mm:ss");
}

/// <summary>
/// Runs the idea queue with two Claude sessions. The WORKER is an interactive coding CLI in a Cockpit pane that does the
/// building. The ORCHESTRATOR is a headless Claude Code session (<see cref="IOrchestratorBrain"/>) that gets the worker's
/// final reply at each turn end, plus Daniel's steers, and answers with one JSON decision that this engine carries out.
/// Hard limits stay in code where the orchestrator cannot argue with them: the Claude usage cap (wait for the reset), the
/// context window (forced handoff into a fresh worker), a cap on decisions per run, and stop. Every run is recorded under
/// <see cref="RunsRoot"/> and, when it ends, committed to the AgentHub repository's archive branch (<see cref="AgentHubArchive"/>). What kind of job the orchestrator runs
/// (its instructions, model, handoff policy) comes from the idea's workflow (<see cref="WorkflowCatalogue"/>).
/// </summary>
public sealed class OrchestratorEngine : IDisposable
{
    private readonly IdeaService _ideaService;
    private readonly GitService _gitService;
    private readonly ProcessRunner _runner;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _disposeCts = new();

    private IBuildWorker? _worker;
    private PaneBuildWorker? _paneWorker;
    private ProjectIdea? _currentIdea;
    private OrchestratorState _state = OrchestratorState.Idle;
    private DateTimeOffset? _nextResetTime;
    private DateTimeOffset _lastUsageCheck = DateTimeOffset.MinValue;
    private UsageSnapshot? _lastUsage;
    private double _contextThreshold = 0.75;
    private double _usageCapThreshold = 0.03;

    private RunRecorder? _run;
    private IOrchestratorBrain? _brain;
    private int _workerSession;
    private bool _handoffRequested;
    private DateTimeOffset _handoffRequestedAt;
    private string? _lastReplyHandled;
    private DateTimeOffset? _waitingSince;
    private int _idleAsks;
    private bool _expectWorkerExit;
    private DateTimeOffset _workerStartedAt;
    private bool _thinking;
    private bool _loopRunning;
    private bool _recovered;
    private bool _staleStatusesFixed;

    /// <summary>Called with the folder a build is about to use (created and git-initialised if needed), so AgentHub can list it among its repositories.</summary>
    public Func<string, Task>? RepositoryInUse { get; set; }

    /// <summary>How long to look for a sent message in the worker's transcript before giving up on checking it.</summary>
    public TimeSpan DeliveryCheckTimeout { get; set; } = TimeSpan.FromSeconds(45);

    /// <summary>Told to the orchestrator in its next message: its previous message reached the worker cut off.</summary>
    private string? _deliveryProblem;
    private OrchestratorWorkflow? _workflow;
    private bool _turnEndedWhileThinking;
    private bool _sentSinceLastTurn;
    private readonly SynchronizationContext? _ui = SynchronizationContext.Current;

    // ---- Settings (public so tests can shorten the clocks) ----

    /// <summary>Model of the orchestrator when the workflow names none. Sonnet: it reads short replies and writes short instructions.</summary>
    public string OrchestratorModel { get; set; } = "sonnet";
    /// <summary>The orchestrator CLI. AGENTHUB_ORCHESTRATOR_COMMAND replaces it, for UI tests that use a scripted orchestrator.</summary>
    public string OrchestratorCommand { get; set; } =
        Environment.GetEnvironmentVariable("AGENTHUB_ORCHESTRATOR_COMMAND") is { Length: > 0 } c ? c : "claude";

    /// <summary>Where runs are written live, one folder each.</summary>
    public string RunsRoot { get; set; } = Environment.GetEnvironmentVariable("AGENTHUB_RUNS_DIR") is { Length: > 0 } d ? d : @"C:\data\agenthub\orchestrator-runs";

    /// <summary>Commits finished runs (and Daniel's workflows) to the repository's archive branch; null keeps runs local only.</summary>
    public AgentHubArchive? Archive { get; set; }

    /// <summary>Where each idea's workflow comes from; null runs every idea as Builder.</summary>
    public WorkflowCatalogue? Workflows { get; set; }

    /// <summary>The workflow of the run in progress.</summary>
    public OrchestratorWorkflow? CurrentWorkflow => _workflow;

    /// <summary>Builds the orchestrator for a new run (tests swap in a scripted one).</summary>
    public Func<RunRecord, string, IOrchestratorBrain>? BrainFactory { get; set; }

    /// <summary>Reads Claude usage (tests swap it out). Null result: unknown.</summary>
    public Func<Task<UsageSnapshot?>> UsageReader { get; set; } = async () =>
        await ClaudeUsageReader.ReadAsync(new UsageProviderDefinition { Id = "claude", Name = "Claude Code", Command = "claude" });

    /// <summary>A turn end counts once the worker has been quiet this long (it may print a little after its prompt returns).</summary>
    public TimeSpan TurnSettle { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>After a wait decision, the orchestrator is asked again once the worker has been silent this long (doubling each time).</summary>
    public TimeSpan IdleRecheck { get; set; } = TimeSpan.FromMinutes(3);

    /// <summary>Time for a new worker CLI to show its prompt before its first instruction is typed.</summary>
    public TimeSpan WorkerStartupDelay { get; set; } = TimeSpan.FromSeconds(4);

    public TimeSpan LoopInterval { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>A handoff turn that takes longer than this is cut short and the fresh session started anyway.</summary>
    public TimeSpan HandoffTimeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Upper bound on orchestrator decisions in one run, so a run that loops cannot burn the whole month.</summary>
    public int MaxDecisionsPerRun { get; set; } = 400;

    public event Action<OrchestratorState>? StateChanged;
    public event Action<ProjectIdea?>? CurrentIdeaChanged;
    public event Action<OrchestratorLog>? LogAdded;
    /// <summary>The orchestrator's latest one-line progress, or its question for Daniel.</summary>
    public event Action<string>? ProgressChanged;
    public event Action<bool>? ThinkingChanged;

    public ObservableCollection<OrchestratorLog> Logs { get; } = [];
    public OrchestratorState State => _state;
    public ProjectIdea? CurrentIdea => _currentIdea;
    public DateTimeOffset? NextResetTime => _nextResetTime;
    public IBuildWorker? Worker => _worker;
    public TerminalPaneControl? ActivePane => _paneWorker?.Pane;
    public string? LatestProgress { get; private set; }
    public bool IsThinking => _thinking;
    /// <summary>The current worker session's number (1 for the first).</summary>
    public int WorkerSession => _workerSession;
    /// <summary>The orchestrator's latest message to the worker (without AgentHub's rules).</summary>
    public string? LastSentToWorker { get; private set; }
    public string? CurrentRunFolder => _run?.Folder;
    public RunRecord? CurrentRun => _run?.Record;
    public SteeringService Steering { get; }

    public double ContextThreshold
    {
        get => _contextThreshold;
        set => _contextThreshold = Math.Clamp(value, 0.40, 0.95);
    }

    public double UsageCapThreshold
    {
        get => _usageCapThreshold;
        set => _usageCapThreshold = Math.Clamp(value, 0.01, 0.20);
    }

    public OrchestratorEngine(
        IdeaService ideaService,
        GitService gitService,
        ProcessRunner runner,
        SteeringService? steering = null)
    {
        _ideaService = ideaService;
        Steering = steering ?? SteeringService.BesideIdeas(ideaService);
        _gitService = gitService;
        _runner = runner;
    }

    /// <summary>The archive for the AgentHub repository this build came from (or Desktop\AgentHub), with its own worktree.</summary>
    public static AgentHubArchive? DefaultArchive(ProcessRunner runner)
    {
        var repo = Environment.GetEnvironmentVariable("AGENTHUB_RUNS_REPO") is { Length: > 0 } r ? r : FindAgentHubRepository();
        if (repo is null) return null;
        var worktree = Environment.GetEnvironmentVariable("AGENTHUB_RUNS_WORKTREE") is { Length: > 0 } w ? w : @"C:\data\agenthub\workflows-archive";
        return new AgentHubArchive(runner, repo, worktree);
    }

    private static string? FindAgentHubRepository()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AgentHub.sln")) && Directory.Exists(Path.Combine(dir.FullName, ".git")))
                return dir.FullName;
        }
        var desktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Desktop", "AgentHub");
        return Directory.Exists(Path.Combine(desktop, ".git")) ? desktop : null;
    }

    // ---- Worker attachment ----

    public void AttachPane(TerminalPaneControl pane)
    {
        if (_paneWorker?.Pane == pane) return;
        var worker = new PaneBuildWorker(pane);
        AttachWorker(worker);
        _paneWorker = worker;
    }

    public void AttachWorker(IBuildWorker worker)
    {
        if (_worker == worker) return;
        if (_worker != null)
        {
            _worker.TurnEnded -= OnWorkerTurnEnded;
            _worker.Exited -= OnWorkerExited;
            _worker.ContextChanged -= OnContextChanged;
        }
        _paneWorker?.Dispose();
        _paneWorker = null;

        _worker = worker;
        _worker.TurnEnded += OnWorkerTurnEnded;
        _worker.Exited += OnWorkerExited;
        _worker.ContextChanged += OnContextChanged;
        Log($"Worker attached: {worker.Label}.");
    }

    // ---- Start / pause / stop ----

    public async Task StartAsync(TerminalPaneControl? pane = null)
    {
        if (pane != null) AttachPane(pane);
        if (_worker == null)
        {
            Log("Cannot start: no terminal pane for the worker.", "Warn");
            return;
        }

        await RecoverInterruptedRunsAsync();
        SetState(OrchestratorState.Building);
        StartLoop();
        Log("Auto-Builder started (Work Mode).");
        await AdvanceQueueAsync();
    }

    public Task BuildIdeaImmediatelyAsync(ProjectIdea idea, TerminalPaneControl pane)
    {
        AttachPane(pane);
        return BuildIdeaImmediatelyAsync(idea);
    }

    public async Task BuildIdeaImmediatelyAsync(ProjectIdea idea, IBuildWorker worker)
    {
        AttachWorker(worker);
        await BuildIdeaImmediatelyAsync(idea);
    }

    private async Task BuildIdeaImmediatelyAsync(ProjectIdea idea)
    {
        await RecoverInterruptedRunsAsync();
        if (_run != null && _run.Record.IdeaId != idea.Id)
            await SuspendRunAsync($"Daniel started \"{idea.Title}\" instead.");

        _currentIdea = idea;
        CurrentIdeaChanged?.Invoke(_currentIdea);
        SetState(OrchestratorState.Building);
        StartLoop();
        Log($"Build requested for \"{idea.Title}\".");

        // Resumed after a pause (or a question for Daniel) with the worker still there: carry on in place.
        if (_run != null && _run.Record.IdeaId == idea.Id && _worker is { IsRunning: true })
        {
            if (idea.Status == IdeaStatus.Paused) { idea.Status = IdeaStatus.Building; idea.StatusNote = "Building"; await _ideaService.UpdateIdeaAsync(idea); }
            await AskAsync(new OrchestratorInput(OrchestratorEvent.Resumed) { Spec = idea.Description, EventDetail = "Daniel pressed Build Now; [SPEC] is the idea's spec as it is now, possibly just updated by him" });
            return;
        }
        await LaunchWorkerSessionAsync(idea);
    }

    public void Pause()
    {
        if (_state is OrchestratorState.Paused or OrchestratorState.Idle) return;
        SetState(OrchestratorState.Paused);
        Log("Auto-Builder paused. The worker is left as it is; it gets nothing new until you resume.");
        _ = AddEventAsync(new RunEvent { Kind = "paused", Detail = "Paused by Daniel." });
    }

    public async Task ResumeAsync()
    {
        if (_state != OrchestratorState.Paused) return;
        SetState(OrchestratorState.Building);
        StartLoop();
        Log("Auto-Builder resumed.");
        await AddEventAsync(new RunEvent { Kind = "resumed" });

        if (_currentIdea is { } idea && idea.Status == IdeaStatus.Paused)
        {
            idea.Status = IdeaStatus.Building;
            idea.StatusNote = "Building";
            await _ideaService.UpdateIdeaAsync(idea);
        }

        if (_run != null && _worker is { IsRunning: false } && _currentIdea is { } current)
        {
            // The builder's session ended while paused (closed pane): Resume is Daniel asking for a fresh one.
            await LaunchWorkerSessionAsync(current, "Daniel resumed after the builder's session ended");
            return;
        }
        if (_run != null && _worker is { IsRunning: true })
        {
            var spec =_currentIdea?.Id is { } id ? (await _ideaService.LoadAsync()).FirstOrDefault(i => i.Id == id)?.Description : null;
            await AskAsync(new OrchestratorInput(OrchestratorEvent.Resumed) { Spec = spec, EventDetail = spec != null ? "[SPEC] is the idea's spec as it is now" : null });
            return;
        }
        await AdvanceQueueAsync();
    }

    public void Stop()
    {
        SetState(OrchestratorState.Idle);
        _handoffRequested = false;
        Log("Auto-Builder stopped.");
        var ideaId = _run?.Record.IdeaId ?? _currentIdea?.Id;
        _ = EndRunAsync("stopped", "Stopped by Daniel.");
        if (ideaId != null) _ = MarkNotBuildingAsync(ideaId, IdeaStatus.Paused, "Stopped");
    }

    public async Task AdvanceQueueAsync()
    {
        if (_state is OrchestratorState.Paused or OrchestratorState.Idle) return;
        if (!await CheckUsageAsync()) return;

        var idea = _currentIdea != null && _currentIdea.Status is IdeaStatus.Building or IdeaStatus.WaitingForReset
            ? _currentIdea
            : await _ideaService.GetNextQueuedAsync();

        if (idea == null)
        {
            SetState(OrchestratorState.Idle);
            _currentIdea = null;
            CurrentIdeaChanged?.Invoke(null);
            Log("All queued ideas are done. Orchestrator idle.");
            return;
        }

        _currentIdea = idea;
        CurrentIdeaChanged?.Invoke(_currentIdea);
        if (_worker == null)
        {
            Log("Waiting for a terminal pane for the worker.", "Warn");
            return;
        }
        await LaunchWorkerSessionAsync(idea);
    }

    // ---- Runs ----

    private async Task<OrchestratorWorkflow> LoadWorkflowAsync(ProjectIdea idea)
    {
        if (Workflows == null) return new OrchestratorWorkflow { Id = OrchestratorWorkflow.DefaultId, Name = "Builder", Model = OrchestratorModel };
        try { return await Workflows.GetAsync(idea.WorkflowId); }
        catch (Exception ex)
        {
            Log($"Could not read workflow {idea.WorkflowId}: {ex.Message}; running it as Builder.", "Warn");
            return new OrchestratorWorkflow { Id = OrchestratorWorkflow.DefaultId, Name = "Builder", Model = OrchestratorModel };
        }
    }

    private IOrchestratorBrain MakeBrain(RunRecord record, string folder, ProjectIdea idea, OrchestratorWorkflow workflow, bool reopen) =>
        BrainFactory?.Invoke(record, folder) ?? new ClaudeOrchestratorBrain(_runner, OrchestratorCommand, record.OrchestratorModel, folder,
            idea.TargetDirectory, OrchestratorMessages.SystemPrompt(idea.Title, idea.TargetDirectory, workflow),
            sessionId: reopen ? record.OrchestratorSessionId : null, alreadyStarted: reopen);

    private async Task BeginRunAsync(ProjectIdea idea)
    {
        var workflow = await LoadWorkflowAsync(idea);
        var now = DateTimeOffset.Now;
        var runId = RunRecorder.NewRunId(idea.Title, now);
        var folder = Path.Combine(RunsRoot, runId);
        var record = new RunRecord
        {
            RunId = runId,
            IdeaId = idea.Id,
            IdeaTitle = idea.Title,
            Spec = idea.Description,
            TargetDirectory = idea.TargetDirectory,
            WorkerCommand = WorkerCommandFor(idea, workflow),
            OrchestratorModel = string.IsNullOrWhiteSpace(workflow.Model) ? OrchestratorModel : workflow.Model,
            Workflow = workflow.Name,
            WorkflowId = workflow.Id,
            WorkflowVersion = workflow.Version(),
            AgentHubBuild = typeof(OrchestratorEngine).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            StartedAt = now,
            TargetHeadAtStart = await GitHeadAsync(idea.TargetDirectory)
        };
        _run = new RunRecorder(folder, record);
        _workflow = workflow;
        await File.WriteAllTextAsync(Path.Combine(folder, "workflow.md"), workflow.Serialize());
        _brain = MakeBrain(record, folder, idea, workflow, reopen: false);
        record.OrchestratorSessionId = _brain.SessionId;
        _workerSession = 0;
        _lastReplyHandled = null;
        await _run.AddAsync(new RunEvent { At = now, Kind = "note", Detail = $"Run started for \"{idea.Title}\" with workflow {workflow.Name} (orchestrator {record.OrchestratorModel})." });
        Log($"Run {runId} started with workflow {workflow.Name}; recording to {folder}.");
    }

    /// <summary>
    /// Picks up the idea's last unfinished run (paused, or AgentHub was closed): same record, same workflow text, and the
    /// same orchestrator session, so it remembers where the job stood. False when there is none.
    /// </summary>
    private async Task<bool> ReopenRunAsync(ProjectIdea idea)
    {
        var found = FindOpenRun(idea.Id);
        if (found is not { } open) return false;
        var (folder, record) = open;

        var workflowFile = Path.Combine(folder, "workflow.md");
        var workflow = File.Exists(workflowFile)
            ? OrchestratorWorkflow.Parse(await File.ReadAllTextAsync(workflowFile), record.WorkflowId)
            : await LoadWorkflowAsync(idea);

        record.Outcome = "running";
        record.EndedAt = null;
        record.Reopened++;
        if (record.WorkerSessions.LastOrDefault() is { EndedAt: null } last) { last.EndedAt = DateTimeOffset.Now; last.EndReason ??= "AgentHub closed"; }
        _run = new RunRecorder(folder, record);
        _workflow = workflow;
        _brain = MakeBrain(record, folder, idea, workflow, reopen: !string.IsNullOrEmpty(record.OrchestratorSessionId));
        if (string.IsNullOrEmpty(record.OrchestratorSessionId)) record.OrchestratorSessionId = _brain.SessionId;
        _workerSession = record.WorkerSessions.Count;
        _lastReplyHandled = null;
        await _run.AddAsync(new RunEvent { Kind = "note", At = DateTimeOffset.Now, Detail = $"Run picked up again ({record.Reopened}×) with workflow {workflow.Name}." });
        Log($"Picking up run {record.RunId} again (workflow {workflow.Name}{(record.CurrentStage is { } st ? $", stage {st}" : "")}).");
        return true;
    }

    /// <summary>The newest run of the idea that is neither finished nor closed.</summary>
    private (string Folder, RunRecord Record)? FindOpenRun(string ideaId)
    {
        if (!Directory.Exists(RunsRoot)) return null;
        return Directory.EnumerateDirectories(RunsRoot)
            .Select(d => (Folder: d, Record: RunRecorder.ReadRecord(Path.Combine(d, "run.json"))))
            .Where(x => x.Record is { Outcome: "running" or "suspended" } r && r.IdeaId == ideaId && r.RunId != _run?.Record.RunId)
            .OrderByDescending(x => x.Record!.StartedAt)
            .Select(x => ((string Folder, RunRecord Record)?)(x.Folder, x.Record!))
            .FirstOrDefault();
    }

    /// <summary>Leaves the run open to be picked up later (AgentHub closing, another idea started): saved, not finished or published.</summary>
    private async Task SuspendRunAsync(string why)
    {
        var run = _run;
        if (run == null) return;
        _run = null;
        _brain = null;
        _workflow = null;
        _waitingSince = null;
        try
        {
            run.Record.Outcome = "suspended";
            run.Record.OutcomeDetail = why;
            run.Record.EndedAt = DateTimeOffset.Now;
            if (run.Record.WorkerSessions.LastOrDefault() is { EndedAt: null } s) { s.EndedAt = DateTimeOffset.Now; s.EndReason = "suspended"; s.TranscriptPath ??= _worker?.TranscriptPath; }
            await run.AddAsync(new RunEvent { At = DateTimeOffset.Now, Kind = "suspended", Detail = why });
            Log($"Run {run.Record.RunId} left open ({why}); building the idea again picks it up.");
        }
        catch (Exception ex) { Log($"Could not save the run: {ex.Message}", "Warn"); }
        await MarkNotBuildingAsync(run.Record.IdeaId, IdeaStatus.Queued, $"Interrupted ({why.TrimEnd('.')}); picks up where it left off");
    }

    /// <summary>
    /// An idea showing Building (or waiting for the usage reset) while nothing builds it is wrong in the queue: set it to what it is.
    /// Ideas in any other state are left alone.
    /// </summary>
    private async Task MarkNotBuildingAsync(string ideaId, IdeaStatus status, string note)
    {
        try
        {
            var idea = (await _ideaService.LoadAsync()).FirstOrDefault(i => i.Id == ideaId);
            if (idea is not { Status: IdeaStatus.Building or IdeaStatus.WaitingForReset }) return;
            idea.Status = status;
            idea.StatusNote = note;
            await _ideaService.UpdateIdeaAsync(idea);
            if (_currentIdea?.Id == ideaId) { _currentIdea.Status = status; _currentIdea.StatusNote = note; }
        }
        catch (Exception ex) { Log($"Could not update the idea's status: {ex.Message}", "Warn"); }
    }

    /// <summary>
    /// Run once when AgentHub starts (the first time the Ideas window opens): nothing can be building yet, so ideas left marked
    /// Building by a crash or an older AgentHub go back in the queue, and runs that can never be picked up are closed.
    /// </summary>
    public async Task RecoverAfterRestartAsync()
    {
        if (_staleStatusesFixed) return;
        _staleStatusesFixed = true;
        if (_run == null && _state == OrchestratorState.Idle)
        {
            foreach (var idea in await _ideaService.LoadAsync())
            {
                if (idea.Status is IdeaStatus.Building or IdeaStatus.WaitingForReset)
                {
                    await MarkNotBuildingAsync(idea.Id, IdeaStatus.Queued, "Interrupted (AgentHub was closed while it was building); picks up where it left off");
                    Log($"\"{idea.Title}\" was still marked Building from before AgentHub restarted; back in the queue.");
                }
            }
        }
        await RecoverInterruptedRunsAsync();
    }

    private async Task EndRunAsync(string outcome, string? detail)
    {
        var run = _run;
        if (run == null) return;
        _run = null;
        _brain = null;
        _workflow = null;
        _waitingSince = null;

        try
        {
            var r = run.Record;
            r.EndedAt = DateTimeOffset.Now;
            if (_worker?.TranscriptPath is { } t && r.WorkerSessions.LastOrDefault() is { TranscriptPath: null } s) s.TranscriptPath = t;
            r.TargetHeadAtEnd = await GitHeadAsync(r.TargetDirectory);
            r.WorkerCommits = await CommitsSinceAsync(r.TargetDirectory, r.StartedAt, 200);
            await run.FinishAsync(outcome, detail);
            Log($"Run {r.RunId} ended ({outcome}). Review: {Path.Combine(run.Folder, "REVIEW.md")}");
        }
        catch (Exception ex)
        {
            Log($"Could not finish the run record: {ex.Message}", "Warn");
            return;
        }

        if (Archive == null) return;
        try { Log(await Archive.PublishRunAsync(run.Folder)); }
        catch (Exception ex) { Log($"Run kept locally; publishing to the repository failed: {ex.Message}", "Warn"); }
    }

    /// <summary>Open runs whose idea is gone or already completed can never be picked up again: close and publish them.</summary>
    private async Task RecoverInterruptedRunsAsync()
    {
        if (_recovered) return;
        _recovered = true;
        if (!Directory.Exists(RunsRoot)) return;
        var ideas = await _ideaService.LoadAsync();
        foreach (var dir in Directory.EnumerateDirectories(RunsRoot))
        {
            var record = RunRecorder.ReadRecord(Path.Combine(dir, "run.json"));
            if (record is not { Outcome: "running" or "suspended" } || record.RunId == _run?.Record.RunId) continue;
            var idea = ideas.FirstOrDefault(i => i.Id == record.IdeaId);
            if (idea != null && idea.Status != IdeaStatus.Completed) continue; // kept to be picked up again
            try
            {
                var recorder = new RunRecorder(dir, record);
                record.WorkerCommits = await CommitsSinceAsync(record.TargetDirectory, record.StartedAt, 200);
                await recorder.FinishAsync("interrupted", idea == null ? "The idea was deleted while the run was open." : "The idea was completed elsewhere while this run was open.");
                if (Archive != null) Log(await Archive.PublishRunAsync(dir));
            }
            catch (Exception ex) { Log($"Could not close run {record.RunId}: {ex.Message}", "Warn"); }
        }
    }

    private async Task AddEventAsync(RunEvent e)
    {
        if (_run == null) return;
        if (e.At == default) e.At = DateTimeOffset.Now;
        e.WorkerSession ??= _workerSession == 0 ? null : _workerSession;
        try { await _run.AddAsync(e); }
        catch (Exception ex) { Log($"Could not write the run record: {ex.Message}", "Warn"); }
    }

    // ---- Worker sessions ----

    /// <summary>
    /// Starts a worker session for the idea: a new run's first session, or a fresh one after a handoff or a usage reset.
    /// The orchestrator writes the first instruction; AgentHub appends its own rules.
    /// </summary>
    private async Task LaunchWorkerSessionAsync(ProjectIdea idea, string? reason = null)
    {
        if (_worker == null) return;
        _handoffRequested = false;
        _waitingSince = null;
        _idleAsks = 0;

        if (!await PrepareWorkspaceAsync(idea)) return;

        var evt = OrchestratorEvent.NewWorkerSession;
        if (_run == null || _run.Record.IdeaId != idea.Id)
        {
            if (FindOpenRun(idea.Id) == null && (await LoadWorkflowAsync(idea)) is { } wf && wf.MissingInputs() is { Count: > 0 } missing)
            {
                // A template like Data Processing needs its pipeline location before it can run: skip the idea, say why.
                idea.Status = IdeaStatus.Paused;
                idea.StatusNote = $"Workflow {wf.Name} needs {string.Join(", ", missing)} filled in";
                await _ideaService.UpdateIdeaAsync(idea);
                Log($"\"{idea.Title}\" skipped: its workflow {wf.Name} needs {string.Join(", ", missing)} filled in (📚 Workflows).", "Warn");
                _currentIdea = null;
                await AdvanceQueueAsync();
                return;
            }
            if (_run != null) await SuspendRunAsync($"Moved on to \"{idea.Title}\".");
            if (await ReopenRunAsync(idea))
            {
                evt = OrchestratorEvent.RunReopened;
                reason ??= "AgentHub was closed or the build was stopped part-way";
            }
            else
            {
                await BeginRunAsync(idea);
                evt = OrchestratorEvent.RunStarted;
            }
        }

        idea.Status = IdeaStatus.Building;
        idea.LastActiveAt = DateTimeOffset.Now;
        idea.StatusNote = $"Building (worker session #{_workerSession + 1})";
        await _ideaService.UpdateIdeaAsync(idea);
        SetState(OrchestratorState.Building);

        if (_worker.IsRunning)
        {
            _expectWorkerExit = true;
            _worker.Stop();
            await Task.Delay(1000);
        }

        var handoff = ReadHandoff(idea);
        var input = new OrchestratorInput(evt)
        {
            EventDetail = reason,
            Spec = idea.Description,
            Handoff = handoff
        };
        await AskAsync(input, startWorker: true);
    }

    private static string WorkerCommandFor(ProjectIdea idea, OrchestratorWorkflow? workflow) =>
        !string.IsNullOrWhiteSpace(workflow?.WorkerCommand) ? workflow.WorkerCommand!
        : string.IsNullOrWhiteSpace(idea.AgentCommand) ? "claude" : idea.AgentCommand;

    private void StartWorker(ProjectIdea idea)
    {
        if (_worker == null || _run == null) return;
        _workerSession++;
        _workerStartedAt = DateTimeOffset.Now;
        _contextBaseline = null;
        _expectWorkerExit = false;
        _lastReplyHandled = null;
        var command = WorkerCommandFor(idea, _workflow);
        _run.Record.WorkerSessions.Add(new WorkerSessionRecord { Number = _workerSession, StartedAt = _workerStartedAt });
        Log($"Starting worker session #{_workerSession}: {command} in {idea.TargetDirectory}.");
        _worker.Start(command, idea.TargetDirectory, $"{_worker.Label} · {idea.Title} (worker #{_workerSession})", idea.Title);
    }

    private async Task<bool> PrepareWorkspaceAsync(ProjectIdea idea)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(idea.TargetDirectory))
            {
                var baseDir = Environment.GetEnvironmentVariable("AGENTHUB_PROJECTS_DIR") is { Length: > 0 } d ? d
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Desktop", "ClaudeCProjects");
                idea.TargetDirectory = Path.Combine(baseDir, SanitizeFolderName(idea.Title));
            }
            Directory.CreateDirectory(idea.TargetDirectory);

            if (!Directory.Exists(Path.Combine(idea.TargetDirectory, ".git")))
            {
                await _runner.RunAsync("git", ["init"], idea.TargetDirectory);
                Log($"Initialised a Git repository in {idea.TargetDirectory}.");
            }

            if (RepositoryInUse is { } inUse)
            {
                try { await inUse(idea.TargetDirectory); }
                catch (Exception ex) { Log($"Could not add {idea.TargetDirectory} to AgentHub's repositories: {ex.Message}", "Warn"); }
            }

            var agenthubDir = Path.Combine(idea.TargetDirectory, ".agenthub");
            Directory.CreateDirectory(agenthubDir);
            var handoffFile = Path.Combine(agenthubDir, "handoff.md");
            // The starter handoff copies the spec. While nobody has worked on it yet ("Not started"), rewrite it from the spec as it is
            // now, so a spec written after the first try (Daniel 2026-11-05: Build Now on the placeholder) does not sit beside a stale Goal.
            var untouchedStarter = File.Exists(handoffFile)
                && await File.ReadAllTextAsync(handoffFile) is var existing
                && existing.StartsWith("# Autonomous Project:", StringComparison.Ordinal)
                && existing.Contains("**Status:** Not started.", StringComparison.Ordinal);
            if (!File.Exists(handoffFile) || untouchedStarter)
            {
                await File.WriteAllTextAsync(handoffFile,
                    $"# Autonomous Project: {idea.Title}\n\n**Goal:** {idea.Description}\n\n**Initialized:** {DateTimeOffset.Now:yyyy-MM-dd HH:mm}\n" +
                    "**Status:** Not started.\n\n---\n\n## Current Plan & Tasks\n- [ ] Establish project scaffolding and architecture\n" +
                    "- [ ] Implement core functionality\n- [ ] Add automated tests\n");
            }
            return true;
        }
        catch (Exception ex)
        {
            Log($"Could not prepare the workspace: {ex.Message}", "Error");
            idea.StatusNote = $"Workspace error: {ex.Message}";
            await _ideaService.UpdateIdeaAsync(idea);
            return false;
        }
    }

    // ---- The orchestrator loop ----

    private void OnWorkerTurnEnded() => _ = HandleTurnEndAsync();

    private async Task HandleTurnEndAsync(bool seenInTranscript = false)
    {
        var worker = _worker;
        if (worker == null || _currentIdea == null || _run == null) return;
        if (_state is not (OrchestratorState.Building or OrchestratorState.HandoffPending)) return;
        if (_thinking) { _turnEndedWhileThinking = true; return; } // handled once the decision in flight is done

        if (!seenInTranscript)
        {
            if (!await SettleAsync(worker)) return;
            _transcriptTurnHandled = worker.ReadTurnEndedAt(); // this turn end is handled here; the loop must not handle it again
        }

        if (_state == OrchestratorState.HandoffPending && _handoffRequested)
        {
            Log("The worker finished its handoff turn; starting a fresh worker session.");
            await PerformHandoffCycleAsync();
            return;
        }
        if (_state != OrchestratorState.Building) return;

        var reply = worker.ReadLastReply();
        var pending = await Steering.GetPendingAsync(_currentIdea.Id);
        if (reply != null && reply == _lastReplyHandled && !_sentSinceLastTurn && pending.Count == 0) return; // same turn reported twice

        await AskAsync(new OrchestratorInput(OrchestratorEvent.WorkerTurnEnded) { WorkerReply = reply ?? "" });
    }

    /// <summary>Waits until the worker has been at its prompt and silent for <see cref="TurnSettle"/>. False when it got busy again or things changed.</summary>
    private async Task<bool> SettleAsync(IBuildWorker worker)
    {
        var session = _workerSession;
        while (true)
        {
            if (_worker != worker || !worker.IsRunning || session != _workerSession || _disposeCts.IsCancellationRequested) return false;
            if (!worker.IsAwaitingInput) return false;
            var silent = worker.OutputSilentFor;
            if (silent >= TurnSettle) return true;
            var wait = TurnSettle - silent;
            await Task.Delay(wait < TimeSpan.FromMilliseconds(200) ? TimeSpan.FromMilliseconds(200) : wait);
        }
    }

    private Task AskAsync(OrchestratorEvent e, bool urgent = false, bool workerBusy = false, string? detail = null) =>
        AskAsync(new OrchestratorInput(e) { UrgentSteer = urgent, WorkerBusy = workerBusy, EventDetail = detail });

    /// <summary>
    /// One orchestrator turn: compose the message, get the decision, carry it out, record it. Serialised, so steers and
    /// turn ends never race. <paramref name="startWorker"/>: the decision is the first instruction of a new worker session.
    /// </summary>
    private async Task AskAsync(OrchestratorInput input, bool startWorker = false)
    {
        OrchestratorDecision? followUp = null;
        await _gate.WaitAsync();
        try
        {
            var idea = _currentIdea;
            var run = _run;
            var brain = _brain;
            if (idea == null || run == null || brain == null) return;

            if (run.Record.OrchestratorCalls >= MaxDecisionsPerRun)
            {
                await PauseForDanielCoreAsync($"The run reached its limit of {MaxDecisionsPerRun} orchestrator decisions. Resume to allow more, or stop it.");
                return;
            }

            var steers = await Steering.GetPendingAsync(idea.Id);
            var deliveryProblem = Interlocked.Exchange(ref _deliveryProblem, null);
            input = input with
            {
                DeliveryProblem = deliveryProblem,
                Steers = steers,
                Status = StatusLine(idea),
                Workflow = WorkflowLine(),
                RecentCommits = await CommitsSinceAsync(idea.TargetDirectory, run.Record.StartedAt, 8)
            };
            var message = OrchestratorMessages.Compose(input);

            SetThinking(true);
            BrainReply reply;
            try
            {
                reply = await brain.DecideAsync(message, _disposeCts.Token);
            }
            catch (OrchestratorBrainException ex)
            {
                await AddEventAsync(new RunEvent { Kind = "error", Trigger = Trigger(input), Detail = ex.Message, Flag = true });
                if (ex.UsageLimit)
                {
                    Log($"The orchestrator hit the Claude usage limit: {ex.Message}", "Warn");
                    _ = CheckUsageAsync(force: true);
                }
                else
                {
                    await PauseForDanielCoreAsync($"The orchestrator failed: {ex.Message}");
                }
                return;
            }
            catch (OperationCanceledException) { return; }
            finally { SetThinking(false); }

            if (_run != run) return; // the run ended while the orchestrator was thinking

            var d = reply.Decision;
            if (steers.Count > 0)
            {
                await Steering.MarkDeliveredAsync(steers);
                run.Record.Steers += steers.Count;
            }
            run.Record.OrchestratorCalls++;
            if (input.Event == OrchestratorEvent.WorkerTurnEnded) { _lastReplyHandled = input.WorkerReply; _sentSinceLastTurn = false; _turnEndedWhileThinking = false; }
            if (d.Progress.Length > 0) SetProgress(d.Progress);

            string? sent = null;
            var flag = reply.Attempts > 1;
            var action = d.Action;
            if (startWorker && action is OrchestratorAction.Wait or OrchestratorAction.Handoff)
            {
                // A new worker must be told something; the orchestrator's choice makes no sense here.
                action = OrchestratorAction.Send;
                d = d with { ToWorker = "Read .agenthub/handoff.md and the code, then continue building toward the goal in it." };
                flag = true;
            }

            // Stages: remember where the job stands, and in an after-each-stage workflow a finished stage means a fresh worker.
            if (d.Stage != null) run.Record.CurrentStage = d.Stage;
            if (d.StageDone && d.Stage != null && !run.Record.StagesDone.Contains(d.Stage))
            {
                run.Record.StagesDone.Add(d.Stage);
                Log($"🧠 Stage done: {d.Stage}.");
            }
            if (d.StageDone && !startWorker && _workflow?.Handoff == WorkflowHandoff.AfterEachStage && action is OrchestratorAction.Send or OrchestratorAction.Wait)
                action = OrchestratorAction.Handoff;

            switch (action)
            {
                case OrchestratorAction.Send:
                    if (startWorker)
                    {
                        StartWorker(idea);
                        await Task.Delay(WorkerStartupDelay);
                        sent = d.ToWorker + " " + OrchestratorMessages.WorkerRules(_workflow);
                    }
                    else sent = d.ToWorker!;

                    if (_worker is { IsRunning: true } w)
                    {
                        _lastSubmitAt = DateTimeOffset.Now;
                        await w.SubmitAsync(sent);
                        _ = CheckDeliveryAsync(w, run, sent);
                        run.Record.MessagesToWorker++;
                        _sentSinceLastTurn = true;
                        _turnEndedWhileThinking = false;
                        _waitingSince = null;
                        _idleAsks = 0;
                        LastSentToWorker = d.ToWorker;
                        Log($"🧠 → worker: {OneLine(d.ToWorker!, 160)}");
                    }
                    else
                    {
                        Log("The worker is not running, so the orchestrator's message was not delivered.", "Warn");
                        sent = null;
                        flag = true;
                    }
                    break;

                case OrchestratorAction.Wait:
                    _waitingSince ??= DateTimeOffset.Now;
                    Log($"🧠 waits: {OneLine(d.Reason, 160)}");
                    break;

                default:
                    followUp = d with { Action = action };
                    break;
            }

            await AddEventAsync(new RunEvent
            {
                Kind = "turn",
                Trigger = Trigger(input),
                Status = input.Status,
                WorkerReply = input.WorkerReply is null ? null : OrchestratorMessages.Cut(input.WorkerReply, OrchestratorMessages.MaxWorkerReplyChars, keepTail: true),
                Steers = steers.Count > 0 ? steers.Select(s => (input.UrgentSteer ? "URGENT: " : "") + s.Text).ToList() : null,
                Action = OrchestratorDecision.ActionName(action),
                ToWorker = d.ToWorker,
                SteerHandling = steers.Count > 0 ? d.SteerHandling : null,
                Reason = d.Reason,
                Progress = d.Progress,
                Stage = d.Stage,
                StageDone = d.StageDone ? true : null,
                Attempts = reply.Attempts,
                Seconds = Math.Round(reply.Elapsed.TotalSeconds, 1),
                SentToWorker = sent,
                Flag = flag
            });
        }
        finally { _gate.Release(); }

        // The worker finished a turn while the orchestrator was deciding something else (a steer): look at it now.
        if (_turnEndedWhileThinking && followUp is null)
        {
            _turnEndedWhileThinking = false;
            _ = HandleTurnEndAsync();
        }

        if (followUp is { } f)
        {
            switch (f.Action)
            {
                case OrchestratorAction.Handoff:
                    await RequestHandoffAsync(f.StageDone && f.Stage != null ? $"stage {f.Stage} is done: {f.Reason}" : $"The orchestrator asked for it: {f.Reason}", forced: false);
                    break;
                case OrchestratorAction.Complete: await CompleteIdeaAsync(f); break;
                case OrchestratorAction.PauseForDaniel: await PauseForDanielAsync(f.Progress.Length > 0 ? f.Progress : f.Reason); break;
            }
        }
    }

    private static string Trigger(OrchestratorInput input) => input.Event switch
    {
        OrchestratorEvent.RunStarted => "run started",
        OrchestratorEvent.NewWorkerSession => "new worker session" + (input.EventDetail is { } d ? $" ({d})" : ""),
        OrchestratorEvent.WorkerTurnEnded => "worker turn ended",
        OrchestratorEvent.WorkerStillIdle => $"worker idle {input.EventDetail}",
        OrchestratorEvent.SteerReceived => input.UrgentSteer ? "urgent steer" : "steer (worker idle)",
        OrchestratorEvent.Resumed => "resumed",
        OrchestratorEvent.RunReopened => "run picked up again" + (input.EventDetail is { } r ? $" ({r})" : ""),
        _ => input.Event.ToString()
    };

    private string? WorkflowLine()
    {
        if (_workflow == null || _run == null) return null;
        var r = _run.Record;
        var line = $"{_workflow.Name}" + (_workflow.Handoff == WorkflowHandoff.AfterEachStage ? " (fresh worker after each stage)" : "");
        if (r.StagesDone.Count > 0) line += " · stages done: " + string.Join(", ", r.StagesDone);
        if (r.CurrentStage is { } cs && !r.StagesDone.Contains(cs)) line += $" · current stage: {cs}";
        return line;
    }

    private string StatusLine(ProjectIdea idea)
    {
        var parts = new List<string>
        {
            $"worker session {Math.Max(_workerSession, 1)}",
            $"decision {(_run?.Record.OrchestratorCalls ?? 0) + 1}",
            $"run time {RunReview.Duration(_run?.Record.Duration ?? TimeSpan.Zero)}"
        };
        if (_worker?.LatestContextReading is { } c)
            parts.Add($"worker context {c.UsedFraction:P0} of {c.WindowTokens / 1000:N0}k{(c.WindowIsAssumed ? " (window assumed)" : "")}, handoff forced at {HandoffThreshold(idea):P0}");
        if (_lastUsage is { Status: UsageCollectionStatus.Available, Limits.Count: > 0 } u)
            parts.Add("Claude usage left: " + string.Join(", ", u.Limits.Select(l => $"{l.Name} {l.RemainingFraction:P0}")) + $" (source: {u.Source})");
        parts.Add($"handoff.md marked [COMPLETED]: {(IsMarkedCompleted(ReadHandoff(idea) ?? "") ? "yes" : "no")}");
        return string.Join(" · ", parts);
    }

    // ---- Decisions that end, pause or hand off ----

    private async Task CompleteIdeaAsync(OrchestratorDecision d)
    {
        var idea = _currentIdea;
        if (idea == null) return;
        Log($"🧠 The orchestrator marked \"{idea.Title}\" complete: {OneLine(d.Reason, 200)}");
        idea.Status = IdeaStatus.Completed;
        idea.StatusNote = "Completed";
        await _ideaService.UpdateIdeaAsync(idea);
        if (_worker is { IsRunning: true })
        {
            _expectWorkerExit = true;
            _worker.Stop();
        }
        await EndRunAsync("completed", d.Reason);
        _currentIdea = null;
        CurrentIdeaChanged?.Invoke(null);
        await AdvanceQueueAsync();
    }

    private async Task PauseForDanielAsync(string question)
    {
        await _gate.WaitAsync();
        try { await PauseForDanielCoreAsync(question); }
        finally { _gate.Release(); }
    }

    private async Task PauseForDanielCoreAsync(string question)
    {
        // Save the idea first: the Ideas window reloads the queue when the state changes.
        if (_currentIdea is { } idea)
        {
            idea.Status = IdeaStatus.Paused;
            idea.StatusNote = "Waiting for Daniel: " + OneLine(question, 200);
            await _ideaService.UpdateIdeaAsync(idea);
        }
        SetState(OrchestratorState.Paused);
        SetProgress("Waiting for you: " + question);
        Log($"⏸ Build paused for you: {question}", "Warn");
        await AddEventAsync(new RunEvent { Kind = "paused", Detail = question, Flag = true });
    }

    private async Task RequestHandoffAsync(string reason, bool forced)
    {
        if (_handoffRequested || _worker is not { IsRunning: true } w) return;
        _handoffRequested = true;
        _handoffRequestedAt = DateTimeOffset.Now;
        SetState(OrchestratorState.HandoffPending);
        Log($"Handoff: {reason}", forced ? "Warn" : "Info");
        await AddEventAsync(new RunEvent { Kind = forced ? "forced_handoff" : "handoff", Detail = reason, Flag = forced });
        _lastSubmitAt = DateTimeOffset.Now;
        LastSentToWorker = "[AgentHub] Time to hand off: write .agenthub/handoff.md and commit.";
        await w.SubmitAsync(OrchestratorMessages.HandoffPrompt);
        if (_run != null) _run.Record.MessagesToWorker++;
    }

    private async Task PerformHandoffCycleAsync(string why = "after a handoff")
    {
        var idea = _currentIdea;
        if (idea == null || _worker == null) return;

        _handoffRequested = false;
        idea.HandoffCount++;
        idea.LastActiveAt = DateTimeOffset.Now;
        var handoff = ReadHandoff(idea);
        if (handoff != null) idea.LastHandoffSummary = handoff.Length > 500 ? handoff[..500] + "..." : handoff;
        idea.StatusNote = $"Handoff #{idea.HandoffCount}";
        await _ideaService.UpdateIdeaAsync(idea);

        if (_run != null)
        {
            _run.Record.Handoffs++;
            if (_run.Record.WorkerSessions.LastOrDefault() is { EndedAt: null } s)
            {
                s.EndedAt = DateTimeOffset.Now;
                s.EndReason = "handoff";
                s.TranscriptPath ??= _worker.TranscriptPath;
            }
        }

        if (_worker.IsRunning)
        {
            _expectWorkerExit = true;
            _worker.Stop();
        }
        await Task.Delay(2500);
        if (!await CheckUsageAsync()) return;
        if (_state == OrchestratorState.HandoffPending) SetState(OrchestratorState.Building);
        await LaunchWorkerSessionAsync(idea, why);
    }

    private void OnContextChanged(ContextReading? reading)
    {
        var idea = _currentIdea;
        if (reading == null || idea == null || _state != OrchestratorState.Building || _handoffRequested) return;
        // A reading from an earlier session in the same pane is not this worker's.
        if (reading.RecordedAt < _workerStartedAt) return;
        var threshold = HandoffThreshold(idea);
        // The session's first reading is what a fresh CLI already uses (system prompt, tools, CLAUDE.md: about 46k tokens for
        // Claude Code). Over the limit already, a handoff would only start another session over it, in a loop: give this
        // session half the room it has left instead, and say so once.
        if (_contextBaseline is not { } baseline)
        {
            _contextBaseline = baseline = reading.UsedFraction;
            if (baseline >= threshold)
            {
                Log($"A fresh worker already uses {baseline:P0} of its context window ({reading.UsedTokens:N0} of {reading.WindowTokens:N0} tokens), over the {threshold:P0} handoff limit. " +
                    "Handing off now would loop, so this session hands off at " + $"{(baseline + 1) / 2:P0}. Raise claudeContextWindowTokens or the limit.", "Warn");
                _ = AddEventAsync(new RunEvent { Kind = "note", Detail = $"Context window too small: a fresh worker starts at {baseline:P0}, over the {threshold:P0} limit.", Flag = true });
            }
            return;
        }
        if (baseline >= threshold) threshold = (baseline + 1) / 2;
        if (reading.UsedFraction >= threshold)
            _ = RequestHandoffAsync($"worker context window at {reading.UsedFraction:P0} (limit {threshold:P0}, {reading.UsedTokens:N0} tokens)", forced: true);
    }

    /// <summary>When AgentHub last typed a message to the worker, and the transcript turn end already acted on.</summary>
    private DateTimeOffset _lastSubmitAt;
    private DateTimeOffset? _transcriptTurnHandled;

    /// <summary>The current worker session's first context reading (its starting size); null until it arrives.</summary>
    private double? _contextBaseline;

    /// <summary>The workflow's context threshold when it sets one, else the idea's.</summary>
    private double HandoffThreshold(ProjectIdea idea) =>
        _workflow?.ContextThreshold ?? (idea.ContextHandoffThreshold > 0 ? idea.ContextHandoffThreshold : _contextThreshold);

    private async void OnWorkerExited()
    {
        if (_expectWorkerExit) { _expectWorkerExit = false; return; }
        var idea = _currentIdea;
        if (idea == null || _run == null) return;

        if (_state == OrchestratorState.HandoffPending && _handoffRequested)
        {
            await PerformHandoffCycleAsync();
            return;
        }
        if (_state != OrchestratorState.Building) return;

        // A session AgentHub did not end: Daniel closed its pane (or typed /exit), or the CLI died. Never start another one on
        // our own (Daniel 2026-11-05: "can you cancel everything it keeps running sessions" after closing panes got three
        // fresh workers in a minute). Pause; Build Now / Resume starts a fresh worker that picks up from the handoff.
        var quick = DateTimeOffset.Now - _workerStartedAt < TimeSpan.FromMinutes(1);
        await AddEventAsync(new RunEvent { Kind = "error", Detail = $"The worker session ended without AgentHub ending it{(quick ? " (within a minute of starting)" : "")}.", Flag = true });
        if (_run.Record.WorkerSessions.LastOrDefault() is { EndedAt: null } s) { s.EndedAt = DateTimeOffset.Now; s.EndReason = "exited"; s.TranscriptPath ??= _worker?.TranscriptPath; }
        Log("The builder's session ended (its pane was closed or the CLI exited). Build paused: nothing new is started until you press Build Now or Resume.", "Warn");
        await PauseForDanielAsync(quick
            ? "The builder's session ended straight after it started. Check that the CLI runs in that folder, then press Build Now."
            : "The builder's session ended (pane closed or CLI exited). Press Build Now to carry on with a fresh builder.");
    }

    // ---- Delivery ----

    private static string Squash(string text) => System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();

    /// <summary>
    /// What the transcript shows of a sent message: "whole", "cut" (its end arrived without its start), or null (not seen yet).
    /// Claude Code wraps a pasted message in &lt;pasted_content&gt;, so the check looks for the start and end inside it.
    /// </summary>
    public static string? DeliveryOf(string sent, string? received)
    {
        if (string.IsNullOrWhiteSpace(received)) return null;
        var want = Squash(sent);
        var got = Squash(received);
        var head = want[..Math.Min(60, want.Length)];
        var tail = want[^Math.Min(40, want.Length)..];
        if (got.Contains(head, StringComparison.Ordinal)) return "whole";
        if (got.Contains(tail, StringComparison.Ordinal)) return "cut";
        return null;
    }

    /// <summary>Looks for a sent message in the worker's transcript; when only its end arrived, records it and tells the orchestrator.</summary>
    private async Task CheckDeliveryAsync(IBuildWorker worker, RunRecorder run, string sent)
    {
        var until = DateTimeOffset.Now + DeliveryCheckTimeout;
        while (DateTimeOffset.Now < until && !_disposeCts.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(DeliveryCheckTimeout < TimeSpan.FromSeconds(5) ? 0.1 : 2), _disposeCts.Token); }
            catch (OperationCanceledException) { return; }
            string? received;
            try { received = worker.ReadLastPrompt(); } catch { return; }
            switch (DeliveryOf(sent, received))
            {
                case "whole": return;
                case "cut":
                    var got = Squash(received!);
                    var shown = got.Length > 200 ? got[..200] + "…" : got;
                    _deliveryProblem = $"Your previous message reached the worker cut off. It received only: \"{shown}\". Send again what was missing.";
                    Log($"⚠ The worker received the orchestrator's message cut off ({got.Length} of {Squash(sent).Length} characters). The orchestrator is told at its next turn.", "Warn");
                    if (_run == run) await AddEventAsync(new RunEvent { Kind = "delivery_cut", Detail = $"Worker received {got.Length} of {Squash(sent).Length} characters: {shown}", Flag = true });
                    return;
            }
        }
    }

    // ---- Steering ----

    /// <summary>
    /// Daniel's message for the orchestrator. It never goes to the worker directly: the orchestrator gets it at the worker's
    /// next turn end (straight away when <paramref name="urgent"/>, or when the worker is already idle) and decides how
    /// to pass it on.
    /// </summary>
    public async Task<SteeringNote> SteerAsync(ProjectIdea idea, string text, bool urgent)
    {
        var note = await Steering.AddAsync(idea.Id, text);
        var live = _currentIdea?.Id == idea.Id && _run != null && _worker is { IsRunning: true } && _state == OrchestratorState.Building;
        var idle = live && _worker!.IsAwaitingInput && _worker.OutputSilentFor >= TimeSpan.FromSeconds(5);

        if (live && (urgent || idle) && !_thinking)
        {
            Log(urgent ? "Urgent steer: asking the orchestrator now." : "The worker is idle: asking the orchestrator now.");
            _ = AskAsync(OrchestratorEvent.SteerReceived, urgent: urgent, workerBusy: !idle);
        }
        else if (live)
            Log("Steer queued: the orchestrator gets it when the worker next finishes a turn.");
        else if (_currentIdea?.Id == idea.Id && _state == OrchestratorState.Paused)
            Log("Steer queued: the orchestrator gets it when you resume.");
        else
            Log($"Steer queued for \"{idea.Title}\": the orchestrator gets it when that build next runs.");
        return note;
    }

    // ---- Usage ----

    public Task<bool> CheckUsageAsync() => CheckUsageAsync(force: false);

    private async Task<bool> CheckUsageAsync(bool force)
    {
        UsageSnapshot? snapshot;
        try { snapshot = await UsageReader(); }
        catch (Exception ex)
        {
            Log($"Usage check notice: {ex.Message}", "Warn");
            return true; // a network blip does not stop the build
        }
        _lastUsageCheck = DateTimeOffset.Now;
        _lastUsage = snapshot;

        var exhausted = snapshot is { Status: UsageCollectionStatus.Available }
            ? snapshot.Limits.FirstOrDefault(l => l.RemainingFraction <= _usageCapThreshold)
            : null;

        if (exhausted == null && force && _state == OrchestratorState.Building)
        {
            // The CLI said it hit a limit but the numbers disagree: wait a little and let the next check decide.
            exhausted = new UsageLimit("CLI-reported limit", 0, DateTimeOffset.Now.AddMinutes(15), null);
        }

        if (exhausted != null)
        {
            if (_state == OrchestratorState.WaitingForReset) return false;
            _nextResetTime = exhausted.ResetsAt ?? DateTimeOffset.Now.AddHours(1);
            Log($"Claude usage cap reached ({exhausted.Name}: {exhausted.RemainingFraction:P0} left, source: {snapshot?.Source ?? "the CLI"}). Waiting until {_nextResetTime:t}.", "Warn");
            await AddEventAsync(new RunEvent { Kind = "usage_wait", Detail = $"{exhausted.Name} at {exhausted.RemainingFraction:P0}; waiting until {_nextResetTime:yyyy-MM-dd HH:mm}.", Flag = true });

            if (_state is OrchestratorState.Building or OrchestratorState.HandoffPending && _worker is { IsRunning: true } w)
            {
                await w.SubmitAsync(OrchestratorMessages.UsageWrapUpPrompt);
                await Task.Delay(TimeSpan.FromSeconds(90));
                if (w.IsRunning) { _expectWorkerExit = true; w.Stop(); }
                if (_run?.Record.WorkerSessions.LastOrDefault() is { EndedAt: null } s) { s.EndedAt = DateTimeOffset.Now; s.EndReason = "usage cap"; s.TranscriptPath ??= w.TranscriptPath; }
            }
            if (_currentIdea != null)
            {
                _currentIdea.Status = IdeaStatus.WaitingForReset;
                _currentIdea.StatusNote = $"Waiting for the Claude reset at {_nextResetTime:t}";
                await _ideaService.UpdateIdeaAsync(_currentIdea);
            }
            _handoffRequested = false;
            SetState(OrchestratorState.WaitingForReset);
            return false;
        }

        if (_state == OrchestratorState.WaitingForReset)
        {
            Log("Claude usage is available again; carrying on.");
            _nextResetTime = null;
            await AddEventAsync(new RunEvent { Kind = "usage_resumed" });
            SetState(OrchestratorState.Building);
            if (_currentIdea != null)
            {
                _currentIdea.Status = IdeaStatus.Building;
                await _ideaService.UpdateIdeaAsync(_currentIdea);
                await LaunchWorkerSessionAsync(_currentIdea, "after the Claude usage reset");
            }
            else await AdvanceQueueAsync();
        }
        return true;
    }

    // ---- Background loop ----

    private void StartLoop()
    {
        if (_loopRunning) return;
        _loopRunning = true;
        _ = LoopAsync();
    }

    private async Task LoopAsync()
    {
        try
        {
            while (!_disposeCts.IsCancellationRequested && _state != OrchestratorState.Idle)
            {
                await Task.Delay(LoopInterval, _disposeCts.Token);
                try { await TickAsync(); }
                catch (Exception ex) { Log($"Orchestrator loop: {ex.Message}", "Warn"); }
            }
        }
        catch (OperationCanceledException) { }
        finally { _loopRunning = false; }
    }

    private async Task TickAsync()
    {
        var now = DateTimeOffset.Now;

        if (_state == OrchestratorState.WaitingForReset)
        {
            if (_nextResetTime is { } reset && now >= reset)
            {
                Log("Reset time reached; checking Claude usage.");
                _nextResetTime = now.AddMinutes(5); // re-check every 5 minutes until the numbers agree
                await CheckUsageAsync();
            }
            return;
        }

        if (_state is OrchestratorState.Building && now - _lastUsageCheck >= TimeSpan.FromSeconds(60))
            await CheckUsageAsync();

        // The terminal can miss a turn end (a message queued during a busy turn runs as the next one without the CLI ever going
        // quiet in between). The transcript records every turn end: one after our last message, settled, counts as the signal.
        if (_state is OrchestratorState.Building or OrchestratorState.HandoffPending && !_thinking && (_sentSinceLastTurn || _handoffRequested)
            && _worker is { IsRunning: true } tw && tw.ReadTurnEndedAt() is { } ended && ended > _lastSubmitAt && now - ended >= TurnSettle
            && ended != _transcriptTurnHandled)
        {
            _transcriptTurnHandled = ended;
            Log("The worker's transcript shows its turn ended (the terminal did not signal it); carrying on.");
            await HandleTurnEndAsync(seenInTranscript: true);
            return;
        }

        if (_state == OrchestratorState.HandoffPending && _handoffRequested && now - _handoffRequestedAt > HandoffTimeout)
        {
            Log("The handoff turn took too long; starting the fresh session anyway.", "Warn");
            await AddEventAsync(new RunEvent { Kind = "forced_handoff", Detail = "Handoff turn timed out.", Flag = true });
            await PerformHandoffCycleAsync();
            return;
        }

        // The orchestrator chose to wait and the worker has gone quiet: ask again, backing off each time.
        if (_state == OrchestratorState.Building && _waitingSince != null && !_thinking && _worker is { IsRunning: true, IsAwaitingInput: true } w)
        {
            var due = TimeSpan.FromTicks(IdleRecheck.Ticks * (1L << Math.Min(_idleAsks, 4)));
            if (w.OutputSilentFor >= due)
            {
                _idleAsks++;
                await AskAsync(OrchestratorEvent.WorkerStillIdle, detail: RunReview.Duration(w.OutputSilentFor));
            }
        }
    }

    // ---- GitHub sync (unchanged) ----

    /// <summary>
    /// Syncs the AgentHub repository with GitHub (origin/main), pulls latest, and compiles/publishes the application.
    /// </summary>
    public async Task<(bool Succeeded, string Message)> SyncAgentHubFromGitHubAsync(
        string? repoPath = null,
        Action<string>? progress = null)
    {
        repoPath ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Desktop", "AgentHub");

        if (!Directory.Exists(repoPath))
            return (false, $"AgentHub repository folder not found at: {repoPath}");

        progress?.Invoke("Fetching latest changes from origin...");
        var fetchResult = await _gitService.FetchAsync(repoPath);
        if (fetchResult.ExitCode != 0)
            return (false, $"Git fetch failed: {fetchResult.StdErr}");

        progress?.Invoke("Pulling latest commits (fast-forward)...");
        var pullResult = await _gitService.PullAsync(repoPath);
        if (pullResult.ExitCode != 0)
            return (false, $"Git pull failed: {pullResult.StdErr}");

        progress?.Invoke($"Git pull successful: {pullResult.StdOut.Trim()}");

        progress?.Invoke("Building AgentHub solution...");
        var slnPath = Path.Combine(repoPath, "AgentHub.sln");
        var buildResult = await _runner.RunAsync("dotnet", ["build", slnPath, "-c", "Release"], repoPath);
        if (buildResult.ExitCode != 0)
            return (false, $"dotnet build failed: {buildResult.StdErr}");

        progress?.Invoke("AgentHub repository is fully up-to-date and compiled!");
        return (true, "AgentHub repository updated and built successfully from GitHub.");
    }

    // ---- Helpers ----

    private static string? ReadHandoff(ProjectIdea idea)
    {
        try
        {
            var path = Path.Combine(idea.TargetDirectory, ".agenthub", "handoff.md");
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private async Task<string?> GitHeadAsync(string dir)
    {
        try
        {
            var r = await _runner.RunAsync("git", ["rev-parse", "--short", "HEAD"], dir);
            return r.ExitCode == 0 ? r.StdOut.Trim() : null;
        }
        catch { return null; }
    }

    private async Task<List<string>> CommitsSinceAsync(string dir, DateTimeOffset since, int max)
    {
        try
        {
            var r = await _runner.RunAsync("git", ["log", "--oneline", "--no-decorate", $"--since={since:yyyy-MM-ddTHH:mm:sszzz}", $"-{max}"], dir);
            return r.ExitCode == 0 ? r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList() : [];
        }
        catch { return []; }
    }

    private void SetState(OrchestratorState newState)
    {
        if (_state == newState) return;
        _state = newState;
        StateChanged?.Invoke(_state);
    }

    private void SetThinking(bool thinking)
    {
        if (_thinking == thinking) return;
        _thinking = thinking;
        ThinkingChanged?.Invoke(thinking);
    }

    private void SetProgress(string progress)
    {
        LatestProgress = progress;
        if (_run != null) _run.Record.LastProgress = progress;
        ProgressChanged?.Invoke(progress);
    }

    private void Log(string message, string level = "Info")
    {
        var entry = new OrchestratorLog(DateTimeOffset.Now, message, level);
        // Logs is bound to the Ideas window: change it on the UI thread only.
        if (_ui != null && SynchronizationContext.Current != _ui)
        {
            _ui.Post(_ => AddLog(entry), null);
            return;
        }
        AddLog(entry);
    }

    private void AddLog(OrchestratorLog entry)
    {
        Logs.Add(entry);
        if (Logs.Count > 300) Logs.RemoveAt(0);
        LogAdded?.Invoke(entry);
    }

    private static string OneLine(string text, int max)
    {
        var line = text.Replace("\r", "").Replace('\n', ' ').Trim();
        return line.Length > max ? line[..max].TrimEnd() + "…" : line;
    }

    /// <summary>
    /// True when the handoff opens with a [COMPLETED] line. Only the top counts: agents often quote the
    /// instruction ("put [COMPLETED] at the top when done") further down in their notes.
    /// </summary>
    public static bool IsMarkedCompleted(string handoff) =>
        handoff.Split('\n')
            .Select(l => l.Trim().TrimStart('#', '*', '>', ' '))
            .Where(l => l.Length > 0)
            .Take(3)
            .Any(l => l.StartsWith("[COMPLETED]", StringComparison.OrdinalIgnoreCase));

    private static string SanitizeFolderName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Where(c => !invalid.Contains(c)).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(clean) ? "ProjectApp" : clean;
    }

    public void Dispose()
    {
        _disposeCts.Cancel();
        if (_run != null)
        {
            // Leave the run open so building the idea again picks it up (the next day, say); bounded, because the app is closing.
            try { Task.Run(() => SuspendRunAsync("AgentHub was closed during the run.")).Wait(TimeSpan.FromSeconds(10)); }
            catch { }
        }
        if (_worker != null)
        {
            _worker.TurnEnded -= OnWorkerTurnEnded;
            _worker.Exited -= OnWorkerExited;
            _worker.ContextChanged -= OnContextChanged;
        }
        _paneWorker?.Dispose();
    }
}
