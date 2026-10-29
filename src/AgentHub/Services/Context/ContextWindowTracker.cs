using System.Collections.Concurrent;
using System.Windows.Threading;

namespace AgentHub.Services.Context;

/// <summary>
/// Follows one Cockpit session's transcript and reports its context usage and the user's last prompt. Polls on the UI dispatcher, reads off it.
/// Each tracker claims the transcript it binds to so two panes in the same folder never show the same session.
/// After <c>/clear</c> (or a new session in the same CLI) a newer unclaimed transcript appears and the tracker moves to it.
/// </summary>
public sealed class ContextWindowTracker : IDisposable
{
    private static readonly ConcurrentDictionary<string, ContextWindowTracker> Claims = new(StringComparer.OrdinalIgnoreCase);

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(3) };
    private ContextSource _source;
    private string _workingDirectory = "";
    private DateTimeOffset _since;
    private long _configuredClaudeWindow;
    private string? _boundPath;
    private DateTime _boundWriteTime;
    private bool _polling;
    private int _generation;

    /// <summary>Raised on the UI thread with the latest reading, or null when there is nothing to show.</summary>
    public event Action<ContextReading?>? ReadingChanged;

    /// <summary>Raised on the UI thread with the last prompt the user sent, or null when there is none.</summary>
    public event Action<LastPrompt?>? PromptChanged;

    public ContextWindowTracker()
    {
        _timer.Tick += async (_, _) => await PollAsync();
    }

    public void Start(string commandLine, string workingDirectory, DateTimeOffset startedAt, long claudeWindowTokens)
    {
        Stop();
        _source = SessionContextReader.DetectSource(commandLine);
        if (_source == ContextSource.None) return;

        _workingDirectory = workingDirectory;
        _since = startedAt;
        _configuredClaudeWindow = claudeWindowTokens;
        _timer.Start();
    }

    /// <summary>Stops polling and clears the display. The last reading is dropped: an exited session has no live context.</summary>
    public void Stop()
    {
        _timer.Stop();
        _generation++;
        Release();
        _source = ContextSource.None;
        ReadingChanged?.Invoke(null);
        PromptChanged?.Invoke(null);
    }

    private async Task PollAsync()
    {
        if (_polling) return;
        _polling = true;
        var generation = _generation;
        try
        {
            var source = _source;
            var dir = _workingDirectory;
            var since = _since;
            var window = _configuredClaudeWindow;
            var excluded = Claims.Where(c => c.Value != this).Select(c => c.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

            var (path, changed, reading, prompt) = await Task.Run<(string?, bool, ContextReading?, LastPrompt?)>(() =>
            {
                var found = source == ContextSource.ClaudeCode
                    ? SessionContextReader.FindClaudeTranscript(dir, since, excluded)
                    : SessionContextReader.FindCodexRollout(dir, since, excluded);
                if (found is null) return (null, false, null, null);

                // Skip the parse when the bound file has not changed since last poll.
                var writeTime = File.GetLastWriteTimeUtc(found);
                if (found == _boundPath && writeTime == _boundWriteTime) return (found, false, null, null);
                _boundWriteTime = writeTime;

                return source == ContextSource.ClaudeCode
                    ? (found, true, SessionContextReader.ReadClaude(found, window), SessionContextReader.ReadClaudePrompt(found))
                    : (found, true, SessionContextReader.ReadCodex(found), SessionContextReader.ReadCodexPrompt(found));
            });

            if (generation != _generation || path is null) return;

            if (!string.Equals(path, _boundPath, StringComparison.OrdinalIgnoreCase))
            {
                if (!Claims.TryAdd(path, this)) return; // another pane claimed it between scan and now
                Release();
                _boundPath = path;
            }

            if (reading is not null) ReadingChanged?.Invoke(reading);
            // A new transcript (after /clear) has no prompt yet, so null is passed on to clear the old one.
            if (changed) PromptChanged?.Invoke(prompt);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Transient file contention; try again next tick.
        }
        finally
        {
            _polling = false;
        }
    }

    private void Release()
    {
        if (_boundPath is not null) Claims.TryRemove(new KeyValuePair<string, ContextWindowTracker>(_boundPath, this));
        _boundPath = null;
        _boundWriteTime = default;
    }

    public void Dispose()
    {
        _timer.Stop();
        _generation++;
        Release();
    }
}
