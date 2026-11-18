using AgentHub.Services.Context;
using AgentHub.Services.History;
using AgentHub.Services.Timeline;
using AgentHub.Terminal;

namespace AgentHub.Services.Orchestrator;

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
    public bool IsAtSecurityCheck => Pane.TerminalControl.IsAtSecurityCheck;
    public TimeSpan OutputSilentFor => Pane.TerminalControl.OutputSilentFor;
    public string? TranscriptPath => Pane.TranscriptPath;
    public ContextReading? LatestContextReading => Pane.LatestContextReading;

    public event Action? TurnEnded;
    public event Action? Exited;
    public event Action<ContextReading?>? ContextChanged;

    public void Start(string commandLine, string workingDirectory, string title, string sessionName)
    {
        Pane.TerminalControl.AnswerFolderTrust = true;
        Pane.StartSession(commandLine, workingDirectory, title, sessionName, isAgentSession: true);
    }

    public void Stop() => Pane.TerminalControl.StopSession();

    public Task SubmitAsync(string prompt) => Pane.SubmitPromptAsync(prompt);

    public string? ReadLastReply()
    {
        var path = TranscriptPath;
        if (string.IsNullOrEmpty(path)) return null;
        var source = path.Contains(".codex", StringComparison.OrdinalIgnoreCase) ? ContextSource.Codex : ContextSource.ClaudeCode;
        return TranscriptTexts.TryRead(path, source).LastReply;
    }

    public IReadOnlyList<HistoryEntry> ReadHistory()
    {
        var path = TranscriptPath;
        if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return [];
        var source = path.Contains(".codex", StringComparison.OrdinalIgnoreCase) ? ContextSource.Codex : ContextSource.ClaudeCode;
        return SessionHistoryReader.ReadEntries(path, source).Entries;
    }

    public DateTimeOffset? ReadTurnEndedAt()
    {
        var path = TranscriptPath;
        if (string.IsNullOrEmpty(path) || path.Contains(".codex", StringComparison.OrdinalIgnoreCase)) return null;
        return TranscriptTurns.LastTurnEnd(path);
    }

    public SessionLaunch? SessionLaunch
    {
        get
        {
            var t = Pane.TerminalControl;
            if (string.IsNullOrWhiteSpace(t.CommandLine) || string.IsNullOrWhiteSpace(t.WorkingDirectory)) return null;
            var name = string.IsNullOrWhiteSpace(t.SessionName) ? Path.GetFileName(t.WorkingDirectory.TrimEnd('\\', '/')) : t.SessionName;
            return new SessionLaunch(t.CommandLine, t.WorkingDirectory, name, name);
        }
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
