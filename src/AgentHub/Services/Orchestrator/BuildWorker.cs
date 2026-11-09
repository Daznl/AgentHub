using AgentHub.Services.Context;
using AgentHub.Services.History;
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

    /// <summary>The session's conversation so far, oldest first, as its transcript recorded it; empty when unknown.</summary>
    IReadOnlyList<HistoryEntry> ReadHistory();

    /// <summary>When the transcript says the latest turn ended (nothing sent since); null while a turn runs or when unknown.</summary>
    DateTimeOffset? ReadTurnEndedAt() => null;

    /// <summary>How the running session was started, so a fresh one can be started the same way after a handoff; null when unknown.</summary>
    SessionLaunch? SessionLaunch => null;

    /// <summary>A security check (folder trust, permission dialog) is on screen for the owner to answer: type nothing into the session.</summary>
    bool IsAtSecurityCheck => false;
}

/// <summary>What it takes to start a session like the running one again.</summary>
public readonly record struct SessionLaunch(string CommandLine, string WorkingDirectory, string Title, string SessionName);

/// <summary>
/// Claude Code ends every turn in its transcript with a <c>{"type":"system","subtype":"turn_duration"}</c> line. A second
/// turn-end signal beside the terminal going quiet (2026-11-05 live test: a handoff request queued during a busy turn ran
/// as the next turn, its end never registered, and the build sat waiting for the 10-minute timeout).
/// </summary>
public static class TranscriptTurns
{
    /// <summary>The end time of the transcript's latest turn when no prompt (typed or queued) follows it; else null.</summary>
    public static DateTimeOffset? LastTurnEnd(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var take = (int)Math.Min(fs.Length, 256 * 1024);
            fs.Seek(-take, SeekOrigin.End);
            var buffer = new byte[take];
            fs.ReadExactly(buffer);
            return LastTurnEnd(System.Text.Encoding.UTF8.GetString(buffer).Split('\n'));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    public static DateTimeOffset? LastTurnEnd(IEnumerable<string> lines)
    {
        foreach (var line in lines.Reverse())
        {
            if (line.Length == 0 || line[0] != '{') continue;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(line);
                var root = doc.RootElement;
                var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
                if (type == "system" && root.TryGetProperty("subtype", out var st) && st.GetString() == "turn_duration")
                    return root.TryGetProperty("timestamp", out var ts) && ts.TryGetDateTimeOffset(out var at) ? at : null;
                if (type == "queue-operation") return null; // a prompt waiting for the next turn
                if (type == "user" && root.TryGetProperty("message", out var m) && m.TryGetProperty("content", out var c) &&
                    (c.ValueKind == System.Text.Json.JsonValueKind.String ||
                     c.EnumerateArray().Any(x => x.TryGetProperty("type", out var xt) && xt.GetString() == "text")))
                    return null; // a prompt after the last turn end: a turn is running
            }
            catch (System.Text.Json.JsonException) { } // the first line of the tail is usually cut
        }
        return null;
    }
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
