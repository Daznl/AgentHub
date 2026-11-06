using System.Collections.ObjectModel;
using System.IO;
using AgentHub.Models;
using AgentHub.Services.Ideas;
using AgentHub.Services.Usage;
using AgentHub.Terminal;

namespace AgentHub.Services.Orchestrator;

/// <summary>
/// Every orchestrator AgentHub runs, side by side (Daniel 2026-11-06: starting a second orchestration suspended the first;
/// "can we make this as many as we want?"). Each is an <see cref="OrchestratorEngine"/> with its own worker pane, run,
/// conversation and log, working exactly as a lone one did. Build Now starts one per idea; the Auto-Builder is one more that
/// works through the queue, skipping ideas another is building. Runs on the same repository get separate git worktrees.
/// The fleet holds what they share: one usage reading for the one Claude account, the run archive, the workflows, and the
/// tidy-up after a restart.
/// </summary>
public sealed class OrchestratorFleet : IDisposable
{
    private readonly Func<SteeringService, OrchestratorEngine> _newEngine;
    private readonly List<OrchestratorEngine> _engines = [];
    /// <summary>The engine that last built each idea, kept after it ends so its log and conversation can still be read.</summary>
    private readonly Dictionary<string, OrchestratorEngine> _byIdea = [];
    private readonly object _sync = new();
    private bool _recovered;

    /// <param name="newEngine">Makes an engine with AgentHub's settings (archive, workflows, repository hook) and the given steering store; the fleet wires the rest.</param>
    public OrchestratorFleet(IdeaService ideaService, Func<SteeringService, OrchestratorEngine> newEngine, SteeringService? steering = null)
    {
        _newEngine = newEngine;
        Steering = steering ?? SteeringService.BesideIdeas(ideaService);
    }

    /// <summary>One steering store for every engine, so a steer reaches whichever engine builds its idea.</summary>
    public SteeringService Steering { get; }

    /// <summary>Where every engine's workflows come from (set to the same catalogue the engine factory uses).</summary>
    public WorkflowCatalogue? Workflows { get; init; }
    /// <summary>The run archive every engine publishes to (set to the same one the engine factory uses).</summary>
    public AgentHubArchive? Archive { get; init; }
    /// <summary>Where runs are written live.</summary>
    public string RunsRoot { get; init; } = Environment.GetEnvironmentVariable("AGENTHUB_RUNS_DIR") is { Length: > 0 } d ? d : @"C:\data\agenthub\orchestrator-runs";

    /// <summary>A change in any engine (state, idea, progress, thinking, conversation), or an engine added.</summary>
    public event Action? Changed;
    /// <summary>Only the changes that alter the queue: an engine's state or idea, or an engine added.</summary>
    public event Action? StateChanged;
    /// <summary>A log line from any engine, with the engine it came from.</summary>
    public event Action<OrchestratorEngine, OrchestratorLog>? LogAdded;

    /// <summary>Lines not tied to one run (the tidy-up after a restart).</summary>
    public ObservableCollection<OrchestratorLog> Logs { get; } = [];

    public IReadOnlyList<OrchestratorEngine> Engines { get { lock (_sync) return _engines.ToList(); } }

    /// <summary>The engines with a build going (not idle).</summary>
    public IReadOnlyList<OrchestratorEngine> Active => Engines.Where(e => e.State != OrchestratorState.Idle).ToList();

    /// <summary>The Auto-Builder lane, once started.</summary>
    public OrchestratorEngine? AutoBuilder { get { lock (_sync) return _engines.FirstOrDefault(e => e.FollowsQueue); } }

    /// <summary>The engine building the idea now, else the one that built it last (its log and conversation); null if none has.</summary>
    public OrchestratorEngine? EngineFor(string ideaId)
    {
        lock (_sync)
        {
            // The last one only while it has not moved on to another idea (the Auto-Builder does), else its log is not this idea's.
            return _engines.FirstOrDefault(e => e.State != OrchestratorState.Idle && e.CurrentIdea?.Id == ideaId)
                ?? (_byIdea.TryGetValue(ideaId, out var last) && (last.CurrentIdea == null || last.CurrentIdea.Id == ideaId) ? last : null);
        }
    }

    /// <summary>The engine driving this pane's session, if any.</summary>
    public OrchestratorEngine? EngineDriving(TerminalPaneControl pane) =>
        Engines.FirstOrDefault(e => e.State != OrchestratorState.Idle && e.ActivePane == pane);

    /// <summary>True when a running engine drives this pane, so no other engine may take it.</summary>
    public bool IsPaneTaken(TerminalPaneControl pane) => EngineDriving(pane) != null;

    /// <summary>
    /// Builds the idea now in its own orchestrator, beside any others. The idea's engine is reused when it has one (a pause,
    /// a question for Daniel, or a finished run); otherwise a new one. Never touches another idea's run.
    /// </summary>
    public async Task<OrchestratorEngine> BuildNowAsync(ProjectIdea idea, Func<OrchestratorEngine?, TerminalPaneControl?> pane)
    {
        var engine = ReusableFor(idea);
        var target = pane(engine) ?? throw NoPane();
        engine ??= Add(followsQueue: false);
        Remember(idea.Id, engine);
        await engine.BuildIdeaImmediatelyAsync(idea, target);
        return engine;
    }

    /// <summary>Build Now with a worker that is not a Cockpit pane (tests).</summary>
    public async Task<OrchestratorEngine> BuildNowAsync(ProjectIdea idea, IBuildWorker worker)
    {
        var engine = ReusableFor(idea) ?? Add(followsQueue: false);
        Remember(idea.Id, engine);
        await engine.BuildIdeaImmediatelyAsync(idea, worker);
        return engine;
    }

    /// <summary>The idea's own engine when it has one to carry on in; the Auto-Builder only while it is on this idea.</summary>
    private OrchestratorEngine? ReusableFor(ProjectIdea idea) =>
        EngineFor(idea.Id) is { } known && (known.State != OrchestratorState.Idle || !known.FollowsQueue) ? known : null;

    private static InvalidOperationException NoPane() => new(
        "Every Cockpit pane has a running session. Close one (or end its session) so the builder gets a pane of its own.");

    /// <summary>Starts the Auto-Builder lane (or resumes it when paused): it builds queued ideas one after another.</summary>
    public async Task<OrchestratorEngine> StartAutoBuilderAsync(Func<OrchestratorEngine?, TerminalPaneControl?> pane)
    {
        var engine = AutoBuilder;
        if (engine?.State == OrchestratorState.Paused)
        {
            await engine.ResumeAsync();
            return engine;
        }
        var target = pane(engine) ?? throw NoPane();
        engine ??= Add(followsQueue: true);
        await engine.StartAsync(target);
        return engine;
    }

    /// <summary>The Auto-Builder with a worker that is not a Cockpit pane (tests).</summary>
    public async Task<OrchestratorEngine> StartAutoBuilderAsync(IBuildWorker worker)
    {
        var engine = AutoBuilder ?? Add(followsQueue: true);
        engine.AttachWorker(worker);
        await engine.StartAsync();
        return engine;
    }

    /// <summary>
    /// Run once when AgentHub starts (the first time the Orchestration window opens), while nothing builds: ideas left marked
    /// Building go back in the queue and runs that can never be picked up are closed.
    /// </summary>
    public async Task RecoverAfterRestartAsync()
    {
        if (_recovered || Active.Count > 0) return;
        _recovered = true;
        using var housekeeper = Wire(_newEngine(Steering), followsQueue: false);
        housekeeper.LogAdded += Keep;
        try { await housekeeper.RecoverAfterRestartAsync(); }
        finally { housekeeper.LogAdded -= Keep; }

        void Keep(OrchestratorLog entry) => Logs.Add(entry);
    }

    /// <summary>Pauses every running orchestrator (Daniel's "stop everything"): each worker is left as it is.</summary>
    public void PauseAll()
    {
        foreach (var e in Active) e.Pause();
    }

    private OrchestratorEngine Add(bool followsQueue)
    {
        var engine = Wire(_newEngine(Steering), followsQueue);
        engine.StateChanged += _ => { StateChanged?.Invoke(); Changed?.Invoke(); };
        engine.ThinkingChanged += _ => Changed?.Invoke();
        engine.ProgressChanged += _ => Changed?.Invoke();
        engine.ConversationChanged += () => Changed?.Invoke();
        engine.CurrentIdeaChanged += idea =>
        {
            if (idea != null) Remember(idea.Id, engine);
            StateChanged?.Invoke();
            Changed?.Invoke();
        };
        engine.LogAdded += entry => LogAdded?.Invoke(engine, entry);
        lock (_sync) _engines.Add(engine);
        StateChanged?.Invoke();
        Changed?.Invoke();
        return engine;
    }

    /// <summary>A steer for the idea: to the engine building it (which decides when to ask), else queued for its next run.</summary>
    public async Task<SteeringNote> SteerAsync(ProjectIdea idea, string text, bool urgent) =>
        EngineFor(idea.Id) is { } engine ? await engine.SteerAsync(idea, text, urgent) : await Steering.AddAsync(idea.Id, text);

    /// <summary>Pulls and builds AgentHub itself; needs no run, so a short-lived engine does it.</summary>
    public async Task<(bool Succeeded, string Message)> SyncAgentHubFromGitHubAsync(Action<string>? progress)
    {
        using var engine = _newEngine(Steering);
        return await engine.SyncAgentHubFromGitHubAsync(progress: progress);
    }

    private OrchestratorEngine Wire(OrchestratorEngine engine, bool followsQueue)
    {
        engine.FollowsQueue = followsQueue;
        engine.UsageReader = SharedUsage(engine.UsageReader);
        engine.IdeaTakenElsewhere = id => Engines.Any(o => o != engine && o.State != OrchestratorState.Idle && o.CurrentIdea?.Id == id);
        engine.FolderInUseElsewhere = folder => Engines.Any(o => o != engine && o.WorkingDirectory is { } wd && SamePath(wd, folder));
        return engine;
    }

    private void Remember(string ideaId, OrchestratorEngine engine)
    {
        lock (_sync) _byIdea[ideaId] = engine;
    }

    // One Claude account for every engine: one reading at a time, reused for a minute, instead of one per engine.
    private Task<UsageSnapshot?>? _usageInFlight;
    private DateTimeOffset _usageReadAt = DateTimeOffset.MinValue;
    private UsageSnapshot? _usage;

    private Func<Task<UsageSnapshot?>> SharedUsage(Func<Task<UsageSnapshot?>> read) => async () =>
    {
        Task<UsageSnapshot?> task;
        lock (_sync)
        {
            if (DateTimeOffset.Now - _usageReadAt < UsageReuse) return _usage;
            task = _usageInFlight ??= ReadAsync();
        }
        return await task;

        async Task<UsageSnapshot?> ReadAsync()
        {
            try
            {
                var snapshot = await read();
                lock (_sync) { _usage = snapshot; _usageReadAt = DateTimeOffset.Now; }
                return snapshot;
            }
            finally { lock (_sync) _usageInFlight = null; }
        }
    };

    /// <summary>How long one usage reading serves every engine.</summary>
    public TimeSpan UsageReuse { get; set; } = TimeSpan.FromMinutes(1);

    private static bool SamePath(string a, string b)
    {
        try { return string.Equals(Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase); }
        catch { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
    }

    /// <summary>AgentHub is closing: every running engine leaves its run open to be picked up again (all at once, each bounded).</summary>
    public void Dispose()
    {
        var all = Engines;
        Task.WaitAll(all.Select(e => Task.Run(e.Dispose)).ToArray(), TimeSpan.FromSeconds(15));
    }
}
