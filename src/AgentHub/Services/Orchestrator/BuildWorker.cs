using AgentHub.Services.Context;
using AgentHub.Services.Timeline;
using AgentHub.Terminal;

namespace AgentHub.Services.Orchestrator;

/// <summary>
/// The worker session the orchestrator drives: an interactive coding CLI that does the actual building. The engine only
/// talks to it through this interface, so tests can drive the orchestrator loop with a scripted worker.
/// </summary>
public interface IBuildWorker
{
    /// <summary>Short name for logs ("Terminal 2").</summary>
    string Label { get; }
    bool IsRunning { get; }

    /// <summary>The CLI is sitting at its prompt (its turn has ended).</summary>
    bool IsAwaitingInput { get; }

    /// <summary>How long the terminal has printed nothing.</summary>
    TimeSpan OutputSilentFor { get; }

    /// <summary>The session's transcript once known (Claude Code: ~/.claude/projects/…/&lt;id&gt;.jsonl).</summary>
    string? TranscriptPath { get; }

    ContextReading? LatestContextReading { get; }

    /// <summary>Raised when the CLI finishes a turn and waits for input.</summary>
    event Action? TurnEnded;
    event Action? Exited;
    event Action<ContextReading?>? ContextChanged;

    void Start(string commandLine, string workingDirectory, string title, string sessionName);
    void Stop();

    /// <summary>Types a prompt into the session and submits it.</summary>
    Task SubmitAsync(string prompt);

    /// <summary>The worker's final reply of its latest turn, read from its transcript; null when unknown.</summary>
    string? ReadLastReply();

    /// <summary>The last prompt the worker received, as its transcript recorded it; null when unknown.</summary>
    string? ReadLastPrompt();
}

/// <summary>A Cockpit terminal pane as the worker.</summary>
public sealed class PaneBuildWorker : IBuildWorker, IDisposable
{
    public TerminalPaneControl Pane { get; }

    public PaneBuildWorker(TerminalPaneControl pane)
    {
        Pane = pane;
        Pane.TerminalControl.AttentionRequested += RaiseTurnEnded;
        Pane.TerminalControl.SessionExited += RaiseExited;
        Pane.ContextReadingChanged += RaiseContext;
    }

    public string Label => $"Terminal {Pane.PaneIndex}";
    public bool IsRunning => Pane.IsRunning;
    public bool IsAwaitingInput => Pane.TerminalControl.IsAwaitingInput;
    public TimeSpan OutputSilentFor => Pane.TerminalControl.OutputSilentFor;
    public string? TranscriptPath => Pane.TranscriptPath;
    public ContextReading? LatestContextReading => Pane.LatestContextReading;

    public event Action? TurnEnded;
    public event Action? Exited;
    public event Action<ContextReading?>? ContextChanged;

    public void Start(string commandLine, string workingDirectory, string title, string sessionName) =>
        Pane.StartSession(commandLine, workingDirectory, title, sessionName, isAgentSession: true);

    public void Stop() => Pane.TerminalControl.StopSession();

    public Task SubmitAsync(string prompt) => Pane.SubmitPromptAsync(prompt);

    public string? ReadLastReply()
    {
        var path = TranscriptPath;
        if (string.IsNullOrEmpty(path)) return null;
        var source = path.Contains(".codex", StringComparison.OrdinalIgnoreCase) ? ContextSource.Codex : ContextSource.ClaudeCode;
        return TranscriptTexts.TryRead(path, source).LastReply;
    }

    public string? ReadLastPrompt()
    {
        var path = TranscriptPath;
        if (string.IsNullOrEmpty(path)) return null;
        var source = path.Contains(".codex", StringComparison.OrdinalIgnoreCase) ? ContextSource.Codex : ContextSource.ClaudeCode;
        return TranscriptTexts.TryRead(path, source).Prompts.LastOrDefault()?.Text;
    }

    private void RaiseTurnEnded() => TurnEnded?.Invoke();
    private void RaiseExited() => Exited?.Invoke();
    private void RaiseContext(ContextReading? reading) => ContextChanged?.Invoke(reading);

    public void Dispose()
    {
        Pane.TerminalControl.AttentionRequested -= RaiseTurnEnded;
        Pane.TerminalControl.SessionExited -= RaiseExited;
        Pane.ContextReadingChanged -= RaiseContext;
    }
}
