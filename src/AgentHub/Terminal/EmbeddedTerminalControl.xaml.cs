using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AgentHub.Hosting;
using Microsoft.Web.WebView2.Core;
using UserControl = System.Windows.Controls.UserControl;

namespace AgentHub.Terminal;

/// <summary>Session lifecycle state, surfaced to the host pane for the status dot and Stop/Restart buttons.</summary>
public enum TerminalStatus { Idle, Running, Stopped, Failed }

public partial class EmbeddedTerminalControl : UserControl, IDisposable
{
    /// <summary>Raised when the session state changes so the host pane can update the status dot and buttons.</summary>
    public event Action<TerminalStatus>? StatusChanged;
    /// <summary>Raised when the status subtitle (PID / working dir / exit info) changes.</summary>
    public event Action<string>? SubtitleChanged;

    private void RaiseStatus(TerminalStatus status) => StatusChanged?.Invoke(status);
    private void RaiseSubtitle(string subtitle) => SubtitleChanged?.Invoke(subtitle);

    private ITerminalSession? _session;
    private bool _isTerminalReady;
    // A launch asked for before the terminal page was ready; runs on "ready".
    private Action? _pendingLaunch;
    private string _commandLine = string.Empty;
    private string _workingDirectory = string.Empty;
    private string _title = string.Empty;
    private int _cols = 100;
    private int _rows = 30;
    // Bumped on every launch or attach so a slow one that was replaced meanwhile lets go of what it started.
    private int _launchGeneration;
    private bool _disposed;
    // Stream offset of the hosted session's output the page has been given so far (-1 when unknown). Kept on the UI thread in
    // step with what was posted, so a screen saved at close says exactly where its replay should continue.
    private long _postedOffset = -1;
    private int _captureToken;
    private readonly Dictionary<int, TaskCompletionSource<(int Cols, int Rows, string Screen)>> _captures = new();

    private bool _isInitialized;
    private bool _isInitializing;

    public event Action? SessionStarted;
    public event Action? SessionExited;
    /// <summary>Raised when a saved session could not be reattached (its host is gone or busy); the pane offers a resume instead.</summary>
    public event Action<SessionRecord, string>? ReattachFailed;
    public bool IsRunning => _session?.IsRunning == true;
    /// <summary>Command line and folder of the current (or last) session.</summary>
    public string CommandLine => _commandLine;
    public string WorkingDirectory => _workingDirectory;
    /// <summary>The repository or place the current session was started for (its first subtitle), e.g. "AgentHub" or "SSH · host".</summary>
    public string SessionName { get; private set; } = "";
    /// <summary>When the current session's process started: now for a launch, the recorded time for a reattach.</summary>
    public DateTimeOffset StartedAt { get; private set; }

    /// <summary>Starts sessions in host processes that outlive AgentHub. Null, or unavailable, means they run inside AgentHub.</summary>
    public SessionHostService? Host { get; set; }
    /// <summary>The host record of the current session, or null when it runs inside AgentHub.</summary>
    public SessionRecord? HostedRecord { get; private set; }

    // Attention detection: only armed for coding-CLI sessions (not plain shells).
    private readonly AttentionDetector _attention = new();
    private readonly DispatcherTimer _attentionTimer;

    /// <summary>Raised once when the agent CLI stops working and appears to be waiting for the user.</summary>
    public event Action? AttentionRequested;
    /// <summary>Raised when the user responds (types) after attention was requested, or the session ends.</summary>
    public event Action? AttentionCleared;
    /// <summary>Raised with true while the agent CLI is actively producing output, false when it stops.</summary>
    public event Action<bool>? WorkingStateChanged;
    private bool _working;

    /// <summary>True when the running command is a coding agent CLI rather than a plain shell.</summary>
    public bool IsAgentSession { get; set; }
    public bool AttentionEnabled { get; set; } = true;
    public double AttentionIdleSeconds
    {
        get => _attention.IdleThreshold.TotalSeconds;
        set => _attention.IdleThreshold = TimeSpan.FromSeconds(Math.Max(0.5, value));
    }

    public EmbeddedTerminalControl()
    {
        InitializeComponent();
        Loaded += EmbeddedTerminalControl_Loaded;
        Unloaded += EmbeddedTerminalControl_Unloaded;

        _attentionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _attentionTimer.Tick += (_, _) =>
        {
            var now = DateTimeOffset.Now;
            var raise = _attention.Tick(now);
            SetWorking(_attention.IsWorking(now));
            if (raise)
            {
                AttentionRequested?.Invoke();
            }
        };
    }

    private static Task<CoreWebView2Environment>? _sharedEnvTask;
    private static readonly object _envLock = new();

    private static Task<CoreWebView2Environment> GetSharedEnvironmentAsync()
    {
        lock (_envLock)
        {
            if (_sharedEnvTask == null)
            {
                var userData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentHub", "WebView2");
                Directory.CreateDirectory(userData);
                _sharedEnvTask = CoreWebView2Environment.CreateAsync(null, userData);
            }
            return _sharedEnvTask;
        }
    }

    private async void EmbeddedTerminalControl_Loaded(object sender, RoutedEventArgs e)
    {
        if (_isInitialized || _isInitializing) return;
        if (WebView.CoreWebView2 != null)
        {
            _isInitialized = true;
            return;
        }

        _isInitializing = true;
        try
        {
            // Paint the control the terminal's dark colour so it never flashes the
            // default white while the CoreWebView2 environment and page are loading.
            WebView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x0A, 0x0F, 0x17);

            var env = await GetSharedEnvironmentAsync();
            await WebView.EnsureCoreWebView2Async(env);

            if (WebView.CoreWebView2 == null) return;

            WebView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            WebView.CoreWebView2.Settings.IsStatusBarEnabled = false;

            // Auto-grant clipboard read so Ctrl+Shift+V / right-click Paste work
            // without a permission prompt on our own local terminal page.
            WebView.CoreWebView2.PermissionRequested -= CoreWebView2_PermissionRequested;
            WebView.CoreWebView2.PermissionRequested += CoreWebView2_PermissionRequested;

            // Ensure event handler is subscribed strictly once to avoid duplicate keystroke events
            WebView.CoreWebView2.WebMessageReceived -= CoreWebView2_WebMessageReceived;
            WebView.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;

            var htmlPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Terminal", "Assets", "terminal.html");
            if (File.Exists(htmlPath))
            {
                WebView.CoreWebView2.Navigate(new Uri(htmlPath).AbsoluteUri);
            }

            _isInitialized = true;
            Loaded -= EmbeddedTerminalControl_Loaded;
        }
        catch (Exception ex)
        {
            RaiseSubtitle("Failed to initialize terminal: " + ex.Message);
        }
        finally
        {
            _isInitializing = false;
        }
    }

    private void EmbeddedTerminalControl_Unloaded(object sender, RoutedEventArgs e)
    {
        // Keep session running unless explicitly stopped
    }

    public void SetHeader(string title, string subtitle = "")
    {
        // Inner title intentionally not shown; the outer pane header already displays "Terminal N".
        _ = title;
        RaiseSubtitle(subtitle);
    }

    public void StartSession(string commandLine, string workingDirectory, string title = "", string subtitle = "", bool isAgentSession = false)
    {
        _commandLine = commandLine;
        _workingDirectory = workingDirectory;
        _title = title;
        SessionName = subtitle;
        IsAgentSession = isAgentSession;
        SetHeader(title, subtitle);
        Launch(SpawnSession);
    }

    /// <summary>Picks up a session that kept running in its host while AgentHub was closed.</summary>
    public void AttachSession(SessionRecord record)
    {
        _commandLine = record.CommandLine;
        _workingDirectory = record.WorkingDirectory;
        _title = record.Title;
        SessionName = record.SessionName;
        IsAgentSession = record.IsAgent;
        SetHeader(record.Title, record.SessionName);
        Launch(() => AttachAsync(record));
    }

    private void Launch(Action launch)
    {
        if (_isTerminalReady) launch();
        else _pendingLaunch = launch;
    }

    /// <summary>Ends the current session's process (a hosted one too) and forgets it.</summary>
    private void EndCurrent()
    {
        var session = _session;
        _session = null;
        if (session is null) return;
        session.Kill();
        session.Dispose();
    }

    private void ClearTerminal()
    {
        try
        {
            WebView.CoreWebView2?.PostWebMessageAsString(JsonSerializer.Serialize(new { type = "clear" }));
        }
        catch { }
    }

    // Hosted when a host is available, so the process survives AgentHub; inside AgentHub when the host cannot start.
    private async void SpawnSession()
    {
        EndCurrent();
        var generation = ++_launchGeneration;
        RaiseStatus(TerminalStatus.Running);
        ClearTerminal();
        HostedRecord = null;
        StartedAt = DateTimeOffset.Now;

        ITerminalSession? session = null;
        string note = "";
        if (Host is { IsAvailable: true } host)
        {
            var record = new SessionRecord
            {
                Id = SessionStore.NewId(),
                CommandLine = _commandLine,
                WorkingDirectory = _workingDirectory,
                Title = _title,
                SessionName = SessionName,
                IsAgent = IsAgentSession,
                Cols = _cols,
                Rows = _rows,
                StartedAt = StartedAt
            };
            try
            {
                RaiseSubtitle("Starting the session host…");
                var hosted = await host.StartAsync(record);
                if (_disposed)
                {
                    // AgentHub closed while the host was starting: detach and let the next start pick it up.
                    hosted.Dispose();
                    return;
                }
                if (generation != _launchGeneration)
                {
                    // Replaced while the host was starting: nobody will look at this one.
                    hosted.Kill();
                    hosted.Dispose();
                    return;
                }
                session = hosted;
                HostedRecord = record;
            }
            catch (Exception ex)
            {
                note = $"Session host failed ({ex.Message}); this session runs inside AgentHub and ends with it.";
            }
        }

        if (session is null)
        {
            try
            {
                session = LocalSession.Start(_commandLine, _workingDirectory, _cols, _rows);
            }
            catch (Exception ex)
            {
                RaiseStatus(TerminalStatus.Failed);
                RaiseSubtitle("Launch failed: " + ex.Message);
                return;
            }
        }

        Hook(session, replay: null);
        if (session is HostedSession started)
        {
            _postedOffset = started.Offset;
            // The pane may have been resized while the host was starting, with nothing yet to tell; the console still has
            // the size it was started with, and the CLI draws for that one until told otherwise.
            if (_cols != started.Welcome.Cols || _rows != started.Welcome.Rows) started.Resize(_cols, _rows);
        }
        // The title row already names the repository; process id and full path are noise there. Exit and error text still show.
        RaiseSubtitle(note);
        SessionStarted?.Invoke();

        // A freshly-launched TUI (e.g. Claude Code) reads the terminal size at
        // startup. Force a couple of re-fits so it gets the true current size and
        // its bottom input/mode line isn't rendered below the visible area.
        ScheduleRefit();
        FocusTerminal();
    }

    private async void AttachAsync(SessionRecord record)
    {
        EndCurrent();
        var generation = ++_launchGeneration;
        RaiseStatus(TerminalStatus.Running);
        ClearTerminal();
        HostedRecord = record;
        StartedAt = record.StartedAt;

        HostedSession hosted;
        ScreenSnapshot? screen = null;
        try
        {
            if (Host is not { IsAvailable: true } host) throw new InvalidOperationException(Host?.UnavailableReason ?? "The session host is not available.");
            // A dead host has no pipe to wait on; say so at once instead of timing out.
            if (!SessionStore.IsHostAlive(record)) throw new InvalidOperationException("its session host is no longer running");
            RaiseSubtitle("Reattaching…");
            screen = host.Store.LoadScreen(record.Id);
            hosted = await host.AttachAsync(record, screen?.Offset ?? -1);
        }
        catch (Exception ex)
        {
            if (generation != _launchGeneration || _disposed) return;
            ShowEnded(record, ex.Message);
            return;
        }

        if (generation != _launchGeneration || _disposed)
        {
            hosted.Dispose();
            return;
        }

        // Exact when the pane's last screen is on disk and the host still had everything written after it: the terminal is put
        // back as it was and only the output since is played, so nothing is drawn twice or in the wrong place.
        var exact = screen is not null && screen.Offset >= 0 && hosted.ReplayStart == screen.Offset
                    && screen.Cols == hosted.Welcome.Cols && screen.Rows == hosted.Welcome.Rows;
        Restore(hosted.Welcome.Cols, hosted.Welcome.Rows, exact ? screen!.Screen : "", hosted.ReplayText);
        Hook(hosted, replay: null);
        _postedOffset = hosted.Offset;
        if (_cols != hosted.Welcome.Cols || _rows != hosted.Welcome.Rows)
        {
            // The pane is not the size the session had: the console repaints everything for the new size (which also
            // covers a restore that was not exact).
            hosted.Resize(_cols, _rows);
        }
        else if (!exact)
        {
            // Same size, but the screen was rebuilt from raw output that may start mid-frame: a size change and back makes
            // the console repaint everything it shows.
            hosted.Resize(_cols, _rows + 1);
            hosted.Resize(_cols, _rows);
        }
        RaiseSubtitle("");
        SessionStarted?.Invoke();
        ScheduleRefit();
    }

    // A saved session whose host is gone: show its last screen and how it ended, then let the pane offer a resume.
    private void ShowEnded(SessionRecord record, string reason)
    {
        var latest = Host?.Store.Load(record.Id) ?? record;
        try
        {
            var replayPath = Host?.Store.ReplayPath(record.Id);
            var replay = replayPath is not null && File.Exists(replayPath) ? File.ReadAllBytes(replayPath) : [];
            var screen = Host?.Store.LoadScreen(record.Id);
            // The saved screen plus the rest of the replay after it, when the replay the host saved at exit reaches back that far.
            if (screen is not null && screen.Offset >= 0 && latest.ReplayStart >= 0
                && screen.Offset >= latest.ReplayStart && screen.Offset <= latest.ReplayStart + replay.Length
                && screen.Cols == latest.Cols && screen.Rows == latest.Rows)
            {
                var rest = replay.AsSpan((int)(screen.Offset - latest.ReplayStart));
                var skip = 0;
                while (skip < rest.Length && skip < 4 && (rest[skip] & 0xC0) == 0x80) skip++;
                Restore(screen.Cols, screen.Rows, screen.Screen, System.Text.Encoding.UTF8.GetString(rest[skip..]));
            }
            else
            {
                Restore(latest.Cols, latest.Rows, "", System.Text.Encoding.UTF8.GetString(replay));
            }
        }
        catch (IOException) { }

        var how = latest.Error is not null ? $"Could not start: {latest.Error}"
            : latest.ExitCode is { } code ? $"Exited (code {code}) while AgentHub was closed"
            : $"Session lost: {reason}";
        RaiseStatus(latest.ExitCode == 0 ? TerminalStatus.Stopped : TerminalStatus.Failed);
        RaiseSubtitle(how);
        ReattachFailed?.Invoke(latest, how);
    }

    private void Post(string text)
    {
        try
        {
            WebView.CoreWebView2?.PostWebMessageAsString(text);
        }
        catch { }
    }

    /// <summary>
    /// Clears the page's terminal, sizes it as the session's console is (so every escape sequence lands where it did),
    /// writes a saved screen (if any) and then the output written after it, and fits the pane once it is all in.
    /// </summary>
    private void Restore(int cols, int rows, string screen, string output)
    {
        try
        {
            WebView.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "restore", cols, rows, screen, data = output }));
        }
        catch { }
    }

    /// <summary>True while the current session runs in a host, the kind a close lets go of rather than ends.</summary>
    public bool IsHosted => _session is HostedSession { IsRunning: true };

    /// <summary>
    /// The terminal exactly as the page shows it now, with the stream offset it stands at, for the next attach to put back.
    /// Null when there is no hosted session, the page is not up, or it does not answer in time.
    /// </summary>
    public async Task<ScreenSnapshot?> CaptureScreenAsync(TimeSpan timeout)
    {
        if (!IsHosted || WebView.CoreWebView2 is null || !_isTerminalReady) return null;
        var token = ++_captureToken;
        // Read before asking: everything posted so far is in the page's queue ahead of the request, and nothing after it is.
        var offset = _postedOffset;
        var pending = new TaskCompletionSource<(int Cols, int Rows, string Screen)>(TaskCreationOptions.RunContinuationsAsynchronously);
        _captures[token] = pending;
        try
        {
            WebView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "capture", token }));
            var done = await Task.WhenAny(pending.Task, Task.Delay(timeout));
            if (done != pending.Task) return null;
            var (cols, rows, text) = pending.Task.Result;
            return new ScreenSnapshot { Offset = offset, Cols = cols, Rows = rows, Screen = text, SavedAt = DateTimeOffset.Now };
        }
        catch
        {
            return null;
        }
        finally
        {
            _captures.Remove(token);
        }
    }

    /// <summary>Saves the terminal as shown beside the hosted session's record, for the next attach. Called before AgentHub lets go.</summary>
    public async Task SaveScreenAsync(TimeSpan timeout)
    {
        if (HostedRecord is not { } record || Host is null) return;
        var screen = await CaptureScreenAsync(timeout);
        if (screen is null) return;
        try
        {
            Host.Store.SaveScreen(record.Id, screen);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The next attach falls back to the host's replay.
        }
    }

    private void Hook(ITerminalSession session, string? replay)
    {
        _session = session;
        _attention.OnSessionStarted(DateTimeOffset.Now);
        _attentionTimer.IsEnabled = IsAgentSession && AttentionEnabled;
        if (!string.IsNullOrEmpty(replay)) Post(replay);

        // Events of a replaced session (Relaunch, Restart) arrive after the new one has started; drop them,
        // or its late exit marks the new session stopped and its last output lands in the cleared terminal.
        session.OutputDataReceived += (text, offset) =>
        {
            Dispatcher.InvokeAsync(() =>
            {
                if (!ReferenceEquals(_session, session)) return;
                if (offset >= 0) _postedOffset = offset;
                if (text.Length == 0) return;
                _attention.OnOutput(text, DateTimeOffset.Now);
                Post(text);
            });
        };

        session.ProcessExited += exitCode =>
        {
            Dispatcher.InvokeAsync(() =>
            {
                if (!ReferenceEquals(_session, session)) return;
                StopAttentionTracking();
                var lost = session is HostedSession { HostLost: true };
                RaiseStatus(exitCode == 0 ? TerminalStatus.Stopped : TerminalStatus.Failed);
                RaiseSubtitle(lost ? "The session host stopped unexpectedly; see host.log in its session folder." : $"Exited (code {exitCode})");
                SessionExited?.Invoke();
            });
        };

        (session as HostedSession)?.Start();
    }

    /// <summary>Records the transcript the pane found for this session, so a reattach shows its context and prompt at once.</summary>
    public void NoteTranscript(string? path)
    {
        if (HostedRecord is not { } record || Host is null || string.IsNullOrEmpty(path)) return;
        if (string.Equals(record.TranscriptPath, path, StringComparison.OrdinalIgnoreCase)) return;
        record.TranscriptPath = path;
        var store = Host.Store;
        var id = record.Id;
        // The host owns the rest of the record (its pids, the exit code): change only this field in what is on disk.
        _ = Task.Run(() =>
        {
            try
            {
                var latest = store.Load(id);
                if (latest is null) return;
                latest.TranscriptPath = path;
                store.Save(latest);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        });
    }

    /// <summary>
    /// Moves keyboard focus into this terminal so the user can type without clicking the pane first.
    /// Deferred to Input priority so it runs after the click that triggered the launch has finished
    /// (otherwise the Launch button keeps focus).
    /// </summary>
    /// <summary>Sends text input directly into the running terminal process.</summary>
    public void SendInput(string data)
    {
        if (string.IsNullOrEmpty(data)) return;
        var wasWaiting = _attention.IsWaiting;
        _attention.OnUserInput(DateTimeOffset.Now);
        if (wasWaiting) AttentionCleared?.Invoke();
        _session?.Write(data);
    }

    public void FocusTerminal()
    {
        _ = Dispatcher.InvokeAsync(() =>
        {
            WebView.Focus();
            try { WebView.CoreWebView2?.PostWebMessageAsString("{\"type\":\"focus\"}"); }
            catch { }
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void ScheduleRefit()
    {
        _ = Dispatcher.InvokeAsync(async () =>
        {
            // Two passes: one right after launch, one after the CLI's initial paint settles.
            foreach (var delayMs in new[] { 150, 600 })
            {
                await Task.Delay(delayMs);
                try { WebView.CoreWebView2?.PostWebMessageAsString("{\"type\":\"refit\"}"); }
                catch { }
            }
        });
    }

    private void CoreWebView2_PermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs e)
    {
        if (e.PermissionKind == CoreWebView2PermissionKind.ClipboardRead)
        {
            e.State = CoreWebView2PermissionState.Allow;
        }
    }

    private void CoreWebView2_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var raw = e.TryGetWebMessageAsString();
            if (string.IsNullOrEmpty(raw)) return;

            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeProp)) return;

            var type = typeProp.GetString();
            switch (type)
            {
                case "ready":
                    _isTerminalReady = true;
                    if (root.TryGetProperty("cols", out var c)) _cols = c.GetInt32();
                    if (root.TryGetProperty("rows", out var r)) _rows = r.GetInt32();
                    if (_pendingLaunch is { } launch)
                    {
                        _pendingLaunch = null;
                        launch();
                    }
                    break;

                case "resize":
                {
                    var cols = root.TryGetProperty("cols", out var colsProp) ? colsProp.GetInt32() : _cols;
                    var rows = root.TryGetProperty("rows", out var rowsProp) ? rowsProp.GetInt32() : _rows;
                    // Refits often report the size the session already has; resizing ConPTY anyway makes some CLIs redraw and lose their first screen.
                    if (cols == _cols && rows == _rows) break;
                    _cols = cols;
                    _rows = rows;
                    _session?.Resize(_cols, _rows);
                    break;
                }

                case "input":
                    if (root.TryGetProperty("data", out var dataProp))
                    {
                        var data = dataProp.GetString();
                        if (data != null)
                        {
                            var wasWaiting = _attention.IsWaiting;
                            _attention.OnUserInput(DateTimeOffset.Now);
                            if (wasWaiting) AttentionCleared?.Invoke();
                            _session?.Write(data);
                        }
                    }
                    break;

                case "screen":
                    // The page's answer to a capture: the terminal as escape sequences that rebuild it.
                    if (root.TryGetProperty("token", out var tokenProp) && _captures.TryGetValue(tokenProp.GetInt32(), out var capture))
                    {
                        var cols = root.TryGetProperty("cols", out var screenCols) ? screenCols.GetInt32() : _cols;
                        var rows = root.TryGetProperty("rows", out var screenRows) ? screenRows.GetInt32() : _rows;
                        var text = root.TryGetProperty("data", out var screenData) ? screenData.GetString() ?? "" : "";
                        capture.TrySetResult((cols, rows, text));
                    }
                    break;
            }
        }
        catch { }
    }

    private void SetWorking(bool working)
    {
        if (working == _working) return;
        _working = working;
        WorkingStateChanged?.Invoke(working);
    }

    private void StopAttentionTracking()
    {
        _attentionTimer.Stop();
        SetWorking(false);
        var wasWaiting = _attention.IsWaiting;
        _attention.Reset();
        if (wasWaiting) AttentionCleared?.Invoke();
    }

    /// <summary>Kills the running session. Called by the host pane's Stop button.</summary>
    public void StopSession()
    {
        StopAttentionTracking();
        // Dropping the session as well as killing it means its exit event is ignored and cannot overwrite "Stopped by user".
        EndCurrent();
        RaiseStatus(TerminalStatus.Stopped);
        RaiseSubtitle("Stopped by user");
        SessionExited?.Invoke();
    }

    /// <summary>Re-launches the last command. Called by the host pane's Restart button.</summary>
    public void RestartSession()
    {
        if (!string.IsNullOrWhiteSpace(_commandLine))
        {
            SpawnSession();
        }
    }

    /// <summary>
    /// Lets go of the session without ending it: a hosted process keeps running for the next AgentHub to reattach; one
    /// running inside AgentHub ends. Called when AgentHub closes.
    /// </summary>
    public void Detach()
    {
        _launchGeneration++;
        _attentionTimer.Stop();
        var session = _session;
        _session = null;
        session?.Detach();
        ReleaseView();
    }

    /// <summary>Ends the session and releases the control. Called when the pane is closed.</summary>
    public void Dispose()
    {
        _launchGeneration++;
        _attentionTimer.Stop();
        EndCurrent();
        ReleaseView();
    }

    private void ReleaseView()
    {
        _disposed = true;
        Loaded -= EmbeddedTerminalControl_Loaded;
        if (WebView.CoreWebView2 != null)
        {
            try
            {
                WebView.CoreWebView2.WebMessageReceived -= CoreWebView2_WebMessageReceived;
                WebView.CoreWebView2.PermissionRequested -= CoreWebView2_PermissionRequested;
            }
            catch { }
        }
    }
}
