using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Threading;
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
/// Autonomous build orchestrator.
/// Manages the build queue of project ideas, monitors Claude usage caps with automatic reset waiting,
/// and tracks session context windows to perform seamless, clean handoffs into fresh sessions.
/// </summary>
public sealed class OrchestratorEngine : IDisposable
{
    private readonly IdeaService _ideaService;
    private readonly GitService _gitService;
    private readonly ProcessRunner _runner;
    private readonly DispatcherTimer _loopTimer;
    private readonly DispatcherTimer _usageTimer;

    private TerminalPaneControl? _activePane;
    private ProjectIdea? _currentIdea;
    private OrchestratorState _state = OrchestratorState.Idle;
    private DateTimeOffset? _nextResetTime;
    private DateTimeOffset _lastUsageCheck = DateTimeOffset.MinValue;
    private double _contextThreshold = 0.75;
    private double _usageCapThreshold = 0.03;
    private bool _handoffRequested;
    private int _handoffWaitTicks;
    private int _continueNudges;

    /// <summary>
    /// How long a session must be completely silent after a turn ends before it is nudged. Long enough that
    /// a slow tool, or an agent waiting on its own background job (Claude Code wakes itself when one
    /// finishes, and any output restarts this clock), is left alone.
    /// </summary>
    private static readonly TimeSpan ContinueNudgeDelay = TimeSpan.FromMinutes(3);

    /// <summary>Upper bound on "keep going" nudges in one session, so an agent that keeps stopping cannot loop forever.</summary>
    private const int MaxContinueNudgesPerSession = 40;

    /// <summary>Appended to every session prompt: there is no human to answer, and how to say the idea is finished.</summary>
    public const string AutonomyRules =
        "You are running unattended: nobody is watching to answer questions, so make reasonable decisions yourself " +
        "and note them in .agenthub/handoff.md instead of asking. Commit your work to git as you go. " +
        "Only when every requirement is implemented and its tests pass, put the line [COMPLETED] at the top of " +
        ".agenthub/handoff.md.";

    /// <summary>Sent when the agent ends a turn while the idea is not finished.</summary>
    public const string ContinuePrompt =
        "[ORCHESTRATOR]: Keep building. Pick the next unfinished task from .agenthub/handoff.md (add tasks if the list is empty), " +
        "implement and test it, update the handoff, and commit. If a background job of yours is still running, check on it and " +
        "work on something that does not depend on it meanwhile. If you were waiting on a question, choose the sensible option " +
        "yourself and carry on. When everything is done and tested, put [COMPLETED] at the top of .agenthub/handoff.md.";

    public event Action<OrchestratorState>? StateChanged;
    public event Action<ProjectIdea?>? CurrentIdeaChanged;
    public event Action<OrchestratorLog>? LogAdded;

    public ObservableCollection<OrchestratorLog> Logs { get; } = [];
    public OrchestratorState State => _state;
    public ProjectIdea? CurrentIdea => _currentIdea;
    public DateTimeOffset? NextResetTime => _nextResetTime;
    public TerminalPaneControl? ActivePane => _activePane;

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
        ProcessRunner runner)
    {
        _ideaService = ideaService;
        _gitService = gitService;
        _runner = runner;

        _loopTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _loopTimer.Tick += OnLoopTick;

        _usageTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _usageTimer.Tick += async (_, _) => await CheckUsageAsync();
    }

    public void AttachPane(TerminalPaneControl pane)
    {
        if (_activePane == pane) return;

        if (_activePane != null)
        {
            _activePane.ContextReadingChanged -= OnContextReadingChanged;
            _activePane.TerminalControl.AttentionRequested -= OnAttentionRequested;
            _activePane.TerminalControl.SessionExited -= OnSessionExited;
        }

        _activePane = pane;
        _activePane.ContextReadingChanged += OnContextReadingChanged;
        _activePane.TerminalControl.AttentionRequested += OnAttentionRequested;
        _activePane.TerminalControl.SessionExited += OnSessionExited;

        Log($"Attached to Cockpit Terminal {pane.PaneIndex}.");
    }

    public async Task StartAsync(TerminalPaneControl? pane = null)
    {
        if (pane != null) AttachPane(pane);

        if (_activePane == null)
        {
            Log("Cannot start orchestrator: No terminal pane assigned.", "Warn");
            return;
        }

        SetState(OrchestratorState.Building);
        _loopTimer.Start();
        _usageTimer.Start();

        Log("Auto-Builder started (Work Mode enabled).");
        await AdvanceQueueAsync();
    }

    public void Pause()
    {
        if (_state == OrchestratorState.Paused || _state == OrchestratorState.Idle) return;
        SetState(OrchestratorState.Paused);
        _loopTimer.Stop();
        Log("Auto-Builder paused.");
    }

    public async Task ResumeAsync()
    {
        if (_state != OrchestratorState.Paused) return;
        SetState(OrchestratorState.Building);
        _loopTimer.Start();
        Log("Auto-Builder resumed.");
        await AdvanceQueueAsync();
    }

    public void Stop()
    {
        SetState(OrchestratorState.Idle);
        _loopTimer.Stop();
        _usageTimer.Stop();
        _handoffRequested = false;
        Log("Auto-Builder stopped.");
    }

    public async Task BuildIdeaImmediatelyAsync(ProjectIdea idea, TerminalPaneControl pane)
    {
        AttachPane(pane);
        _currentIdea = idea;
        CurrentIdeaChanged?.Invoke(_currentIdea);

        SetState(OrchestratorState.Building);
        _loopTimer.Start();
        _usageTimer.Start();

        Log($"Immediate build requested for idea: \"{idea.Title}\".");
        await LaunchBuildSessionAsync(idea);
    }

    public async Task AdvanceQueueAsync()
    {
        if (_state is OrchestratorState.Paused or OrchestratorState.Idle) return;

        // Check if usage cap is currently active
        var usageOk = await CheckUsageAsync();
        if (!usageOk) return;

        var idea = _currentIdea != null && _currentIdea.Status is IdeaStatus.Building or IdeaStatus.WaitingForReset
            ? _currentIdea
            : await _ideaService.GetNextQueuedAsync();

        if (idea == null)
        {
            SetState(OrchestratorState.Idle);
            _currentIdea = null;
            CurrentIdeaChanged?.Invoke(null);
            Log("All queued project ideas have been processed. Orchestrator idle.");
            return;
        }

        _currentIdea = idea;
        CurrentIdeaChanged?.Invoke(_currentIdea);

        if (_activePane == null)
        {
            Log("Waiting for active terminal pane to run orchestrator.", "Warn");
            return;
        }

        await LaunchBuildSessionAsync(idea);
    }

    private async Task LaunchBuildSessionAsync(ProjectIdea idea)
    {
        if (_activePane == null) return;

        _handoffRequested = false;
        _handoffWaitTicks = 0;
        _continueNudges = 0;

        // Ensure workspace directory exists
        try
        {
            if (string.IsNullOrWhiteSpace(idea.TargetDirectory))
            {
                var baseDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Desktop", "ClaudeCProjects");
                idea.TargetDirectory = Path.Combine(baseDir, SanitizeFolderName(idea.Title));
            }

            Directory.CreateDirectory(idea.TargetDirectory);

            var gitDir = Path.Combine(idea.TargetDirectory, ".git");
            if (!Directory.Exists(gitDir))
            {
                await _runner.RunAsync("git", ["init"], idea.TargetDirectory);
                Log($"Initialized new Git repository in {idea.TargetDirectory}.");
            }

            var agenthubDir = Path.Combine(idea.TargetDirectory, ".agenthub");
            Directory.CreateDirectory(agenthubDir);

            var handoffFile = Path.Combine(agenthubDir, "handoff.md");
            if (!File.Exists(handoffFile))
            {
                var initialHandoff = $"# Autonomous Project: {idea.Title}\n\n" +
                                     $"**Goal:** {idea.Description}\n\n" +
                                     $"**Initialized:** {DateTimeOffset.Now:yyyy-MM-dd HH:mm}\n" +
                                     $"**Status:** Initializing build cycle.\n\n" +
                                     $"---\n\n" +
                                     $"## Current Plan & Tasks\n" +
                                     $"- [ ] Establish project scaffolding and architecture\n" +
                                     $"- [ ] Implement core functionality\n" +
                                     $"- [ ] Add automated tests\n";
                await File.WriteAllTextAsync(handoffFile, initialHandoff);
            }
        }
        catch (Exception ex)
        {
            Log($"Error preparing workspace: {ex.Message}", "Error");
            idea.StatusNote = $"Workspace error: {ex.Message}";
            await _ideaService.UpdateIdeaAsync(idea);
            return;
        }

        idea.Status = IdeaStatus.Building;
        idea.LastActiveAt = DateTimeOffset.Now;
        idea.StatusNote = $"Building (Session #{idea.HandoffCount + 1})";
        await _ideaService.UpdateIdeaAsync(idea);
        SetState(OrchestratorState.Building);

        // Stop any previous session in pane if running
        if (_activePane.IsRunning)
        {
            _activePane.TerminalControl.StopSession();
            await Task.Delay(1000);
        }

        var cliCommand = string.IsNullOrWhiteSpace(idea.AgentCommand) ? "claude" : idea.AgentCommand;
        var title = $"Terminal {_activePane.PaneIndex} Â· {idea.Title} (Session #{idea.HandoffCount + 1})";

        Log($"Launching {cliCommand} in {idea.TargetDirectory} (Session #{idea.HandoffCount + 1})...");
        _activePane.StartSession(cliCommand, idea.TargetDirectory, title, idea.Title, isAgentSession: true);

        // Give the CLI 3 seconds to spin up, then inject the initial or resume prompt
        await Task.Delay(3000);

        string promptText;
        if (idea.HandoffCount == 0)
        {
            promptText =
                $"We are beginning the autonomous build of \"{idea.Title}\".\n" +
                $"Requirements and Specifications:\n{idea.Description}\n\n" +
                $"Please inspect the folder, create a clean architecture, implement the code incrementally with unit tests, " +
                $"and update .agenthub/handoff.md regularly with your progress.";
        }
        else
        {
            promptText =
                $"Resuming autonomous build for \"{idea.Title}\" (Session #{idea.HandoffCount + 1}).\n" +
                $"Please read .agenthub/handoff.md for recent progress and next tasks, inspect the codebase and tests, " +
                $"and continue building the planned features.";
        }

        promptText += " " + AutonomyRules;

        await _activePane.SubmitPromptAsync(promptText);
        Log($"Injected session prompt ({promptText.Length} chars).");
    }

    private void OnContextReadingChanged(ContextReading? reading)
    {
        if (reading == null || _state != OrchestratorState.Building || _handoffRequested || _currentIdea == null)
            return;

        var threshold = _currentIdea.ContextHandoffThreshold > 0
            ? _currentIdea.ContextHandoffThreshold
            : _contextThreshold;

        if (reading.UsedFraction >= threshold)
        {
            Log($"Context window reached {reading.UsedFraction:P0} (threshold {threshold:P0}, {reading.UsedTokens:N0} tokens). Initiating clean handoff...", "Warn");
            _handoffRequested = true;
            SetState(OrchestratorState.HandoffPending);

            var handoffPrompt =
                "\n\n[ORCHESTRATOR NOTICE]: Context window threshold reached (" + $"{reading.UsedFraction:P0}" + ").\n" +
                "Please pause writing new features. Run tests to confirm current status, update .agenthub/handoff.md with:\n" +
                "1. Summary of what was accomplished this session\n" +
                "2. Architecture notes and test results\n" +
                "3. Immediate next tasks for the next session\n" +
                "When .agenthub/handoff.md is saved, end your turn cleanly so a fresh session can take over.\n";

            if (_activePane != null) _ = _activePane.SubmitPromptAsync(handoffPrompt);
        }
    }

    private async void OnAttentionRequested()
    {
        if (_currentIdea == null || _activePane == null) return;

        // If we were waiting for handoff completion
        if (_state == OrchestratorState.HandoffPending && _handoffRequested)
        {
            Log("Agent finished handoff turn. Concluding session and transitioning to fresh context...");
            await PerformHandoffCycleAsync();
            return;
        }

        // If building normally and agent finished a turn, check for completion signal
        if (_state == OrchestratorState.Building)
        {
            var handoffPath = Path.Combine(_currentIdea.TargetDirectory, ".agenthub", "handoff.md");
            if (File.Exists(handoffPath))
            {
                var content = await File.ReadAllTextAsync(handoffPath);
                if (IsMarkedCompleted(content))
                {
                    Log($"Idea \"{_currentIdea.Title}\" marked as completed in handoff.md!");
                    _currentIdea.Status = IdeaStatus.Completed;
                    _currentIdea.StatusNote = "Completed all tasks";
                    await _ideaService.UpdateIdeaAsync(_currentIdea);
                    _activePane.TerminalControl.StopSession();
                    await AdvanceQueueAsync();
                    return;
                }
            }

            await NudgeToContinueAsync();
        }
    }

    /// <summary>
    /// The agent ended a turn but the idea is not finished: after a short pause, if it is still idle and
    /// nothing else has taken over, tell it to carry on with the next task.
    /// </summary>
    private async Task NudgeToContinueAsync()
    {
        var pane = _activePane;
        var idea = _currentIdea;
        if (pane == null || idea == null) return;

        // Wait until the terminal has been silent for the whole delay. Output in between (the agent woke
        // itself) restarts the wait; anything else changing (user typed, handoff or quota pause, session
        // ended, another idea took the pane) abandons it, and the next finished turn schedules a new one.
        while (true)
        {
            var silent = pane.TerminalControl.OutputSilentFor;
            if (silent >= ContinueNudgeDelay) break;
            await Task.Delay(ContinueNudgeDelay - silent + TimeSpan.FromSeconds(1));

            if (_state != OrchestratorState.Building || _handoffRequested || _activePane != pane ||
                _currentIdea != idea || !pane.IsRunning || !pane.TerminalControl.IsAwaitingInput)
                return;
        }

        if (_continueNudges >= MaxContinueNudgesPerSession)
        {
            Log($"Agent stopped {_continueNudges} times this session; starting a fresh session from the handoff instead of nudging again.", "Warn");
            await PerformHandoffCycleAsync();
            return;
        }

        _continueNudges++;
        Log($"Agent ended its turn; nudging it to continue ({_continueNudges}/{MaxContinueNudgesPerSession}).");
        await pane.SubmitPromptAsync(ContinuePrompt);
    }

    private async void OnSessionExited()
    {
        if (_state == OrchestratorState.HandoffPending && _handoffRequested)
        {
            await PerformHandoffCycleAsync();
        }
    }

    private async Task PerformHandoffCycleAsync()
    {
        if (_currentIdea == null || _activePane == null) return;

        _handoffRequested = false;
        _currentIdea.HandoffCount++;
        _currentIdea.LastActiveAt = DateTimeOffset.Now;

        // Read handoff summary
        var handoffPath = Path.Combine(_currentIdea.TargetDirectory, ".agenthub", "handoff.md");
        if (File.Exists(handoffPath))
        {
            try
            {
                var text = await File.ReadAllTextAsync(handoffPath);
                _currentIdea.LastHandoffSummary = text.Length > 500 ? text[..500] + "..." : text;
            }
            catch { }
        }

        _currentIdea.StatusNote = $"Completed Handoff #{_currentIdea.HandoffCount}";
        await _ideaService.UpdateIdeaAsync(_currentIdea);

        Log($"Handoff #{_currentIdea.HandoffCount} recorded. Stopping old session...");
        if (_activePane.IsRunning)
        {
            _activePane.TerminalControl.StopSession();
        }

        await Task.Delay(2500);

        // Check quota before starting next session
        var quotaOk = await CheckUsageAsync();
        if (!quotaOk) return;

        Log($"Starting fresh session #{_currentIdea.HandoffCount + 1} for \"{_currentIdea.Title}\"...");
        await LaunchBuildSessionAsync(_currentIdea);
    }

    public async Task<bool> CheckUsageAsync()
    {
        try
        {
            var provider = new UsageProviderDefinition
            {
                Id = "claude",
                Name = "Claude Code",
                Command = "claude"
            };

            var snapshot = await ClaudeUsageReader.ReadAsync(provider);
            _lastUsageCheck = DateTimeOffset.Now;

            if (snapshot.Status == UsageCollectionStatus.Available && snapshot.Limits.Count > 0)
            {
                // Find limiting quota
                var exhaustedLimit = snapshot.Limits.FirstOrDefault(l => l.RemainingFraction <= _usageCapThreshold);
                if (exhaustedLimit != null)
                {
                    _nextResetTime = exhaustedLimit.ResetsAt ?? DateTimeOffset.Now.AddHours(1);
                    var duration = _nextResetTime.Value - DateTimeOffset.Now;
                    var resetDesc = duration.TotalMinutes > 0
                        ? $"in {(int)duration.TotalHours}h {duration.Minutes}m"
                        : "shortly";

                    Log($"Claude quota cap reached ({exhaustedLimit.Name}: {exhaustedLimit.RemainingFraction:P0} remaining). Pausing until reset {resetDesc} ({_nextResetTime:t}).", "Warn");

                    if (_state == OrchestratorState.Building && _activePane?.IsRunning == true)
                    {
                        await _activePane.SubmitPromptAsync("[ORCHESTRATOR NOTICE]: Claude usage quota exhausted. Please save current work to .agenthub/handoff.md and wrap up.");
                        await Task.Delay(5000);
                        if (_activePane.IsRunning) _activePane.TerminalControl.StopSession();
                    }

                    if (_currentIdea != null)
                    {
                        _currentIdea.Status = IdeaStatus.WaitingForReset;
                        _currentIdea.StatusNote = $"Waiting for Claude reset at {_nextResetTime:t}";
                        await _ideaService.UpdateIdeaAsync(_currentIdea);
                    }

                    SetState(OrchestratorState.WaitingForReset);
                    return false;
                }
            }

            // If we were waiting for reset and quota is now available again
            if (_state == OrchestratorState.WaitingForReset)
            {
                Log("Claude usage quota reset confirmed! Resuming autonomous build...", "Info");
                SetState(OrchestratorState.Building);
                if (_currentIdea != null)
                {
                    _currentIdea.Status = IdeaStatus.Building;
                    await _ideaService.UpdateIdeaAsync(_currentIdea);
                }
                await AdvanceQueueAsync();
                return true;
            }

            return true;
        }
        catch (Exception ex)
        {
            Log($"Usage check notice: {ex.Message}", "Warn");
            return true; // Don't block on network check blips
        }
    }

    private async void OnLoopTick(object? sender, EventArgs e)
    {
        // Periodic check for reset time if waiting
        if (_state == OrchestratorState.WaitingForReset && _nextResetTime.HasValue)
        {
            if (DateTimeOffset.Now >= _nextResetTime.Value)
            {
                Log("Scheduled reset time reached. Checking Claude quota status...");
                await CheckUsageAsync();
            }
        }

        // Failsafe for stuck handoff
        if (_state == OrchestratorState.HandoffPending && _handoffRequested)
        {
            _handoffWaitTicks++;
            if (_handoffWaitTicks > 40) // 120 seconds timeout
            {
                Log("Handoff turn wait timed out (120s). Forcing clean session restart...", "Warn");
                await PerformHandoffCycleAsync();
            }
        }
    }

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

    private void SetState(OrchestratorState newState)
    {
        if (_state == newState) return;
        _state = newState;
        StateChanged?.Invoke(_state);
    }

    private void Log(string message, string level = "Info")
    {
        var entry = new OrchestratorLog(DateTimeOffset.Now, message, level);
        Logs.Add(entry);
        if (Logs.Count > 200) Logs.RemoveAt(0);
        LogAdded?.Invoke(entry);
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
        _loopTimer.Stop();
        _usageTimer.Stop();
        if (_activePane != null)
        {
            _activePane.ContextReadingChanged -= OnContextReadingChanged;
            _activePane.TerminalControl.AttentionRequested -= OnAttentionRequested;
            _activePane.TerminalControl.SessionExited -= OnSessionExited;
        }
    }
}
