using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Color = System.Windows.Media.Color;
using UserControl = System.Windows.Controls.UserControl;

namespace AgentHub.Terminal;

public enum TerminalSessionState
{
    /// <summary>No session has been launched in this terminal yet.</summary>
    Idle,
    Running,
    /// <summary>The process exited normally or was stopped by the user.</summary>
    Stopped,
    /// <summary>The process exited with a non-zero code or failed to launch.</summary>
    Failed
}

public partial class EmbeddedTerminalControl : UserControl, IDisposable
{
    private ConPtySession? _session;
    // Set by Stop() so the process-exit callback reports "Stopped by user" rather than a failure
    // (TerminateProcess makes the child exit with a non-zero code).
    private bool _stopRequested;

    /// <summary>Current lifecycle state; the owning pane renders it (status dot, Stop/Restart buttons).</summary>
    public TerminalSessionState State { get; private set; } = TerminalSessionState.Idle;
    /// <summary>What is running here, e.g. "Terminal 2 · Claude Code" as passed by the launcher.</summary>
    public string SessionLabel { get; private set; } = string.Empty;
    /// <summary>PID and working directory while running; exit/stop/failure reason otherwise.</summary>
    public string StatusDetail { get; private set; } = string.Empty;
    /// <summary>True when a previous command can be relaunched in place.</summary>
    public bool CanRestart => State != TerminalSessionState.Running && !string.IsNullOrWhiteSpace(_commandLine);
    /// <summary>Raised whenever State, SessionLabel or StatusDetail change.</summary>
    public event Action? StatusChanged;
    private bool _isTerminalReady;
    private bool _pendingStart;
    private string _commandLine = string.Empty;
    private string _workingDirectory = string.Empty;
    private int _cols = 100;
    private int _rows = 30;

    private bool _isInitialized;
    private bool _isInitializing;

    public event Action? SessionStarted;
    public event Action? SessionExited;
    public bool IsRunning => _session?.IsRunning == true;

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
            SetState(TerminalSessionState.Failed, "Failed to initialize terminal: " + ex.Message);
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
        SessionLabel = title;
        StatusDetail = subtitle;
        StatusChanged?.Invoke();
    }

    private void SetState(TerminalSessionState state, string detail)
    {
        State = state;
        StatusDetail = detail;
        StatusChanged?.Invoke();
    }

    public void StartSession(string commandLine, string workingDirectory, string title = "", string subtitle = "", bool isAgentSession = false)
    {
        _commandLine = commandLine;
        _workingDirectory = workingDirectory;
        IsAgentSession = isAgentSession;
        SetHeader(title, subtitle);

        if (_isTerminalReady)
        {
            SpawnConPty();
        }
        else
        {
            _pendingStart = true;
        }
    }

    private void SpawnConPty()
    {
        _pendingStart = false;
        _stopRequested = false;
        _session?.Dispose();
        _session = null;

        try
        {
            try
            {
                WebView.CoreWebView2?.PostWebMessageAsString(JsonSerializer.Serialize(new { type = "clear" }));
            }
            catch { }

            _session = ConPtySession.Start(_commandLine, _workingDirectory, _cols, _rows);

            // The working pulse is output-driven for every session (so a plain shell in which the user
            // typed `claude` still glows); only the "waiting for you" alert is limited to agent launches.
            _attention.RaiseAttention = IsAgentSession && AttentionEnabled;
            _attention.OnSessionStarted(DateTimeOffset.Now);
            _attentionTimer.IsEnabled = true;

            _session.OutputDataReceived += text =>
            {
                Dispatcher.InvokeAsync(() =>
                {
                    _attention.OnOutput(text, DateTimeOffset.Now);
                    try
                    {
                        WebView.CoreWebView2?.PostWebMessageAsString(text);
                    }
                    catch { }
                });
            };

            _session.ProcessExited += exitCode =>
            {
                Dispatcher.InvokeAsync(() =>
                {
                    StopAttentionTracking();
                    if (_stopRequested)
                    {
                        SetState(TerminalSessionState.Stopped, "Stopped by user");
                    }
                    else
                    {
                        SetState(exitCode == 0 ? TerminalSessionState.Stopped : TerminalSessionState.Failed, $"Exited (code {exitCode})");
                    }
                    SessionExited?.Invoke();
                });
            };

            SetState(TerminalSessionState.Running, $"PID {_session.ProcessId} · {_workingDirectory}");
            SessionStarted?.Invoke();

            // A freshly-launched TUI (e.g. Claude Code) reads the terminal size at
            // startup. Force a couple of re-fits so it gets the true current size and
            // its bottom input/mode line isn't rendered below the visible area.
            ScheduleRefit();
        }
        catch (Exception ex)
        {
            SetState(TerminalSessionState.Failed, "Launch failed: " + ex.Message);
        }
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
                    if (_pendingStart) SpawnConPty();
                    break;

                case "resize":
                    if (root.TryGetProperty("cols", out var colsProp)) _cols = colsProp.GetInt32();
                    if (root.TryGetProperty("rows", out var rowsProp)) _rows = rowsProp.GetInt32();
                    _session?.Resize(_cols, _rows);
                    break;

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

    /// <summary>Kills the running process but keeps the pane and its last command so it can be restarted.</summary>
    public void Stop()
    {
        _stopRequested = true;
        StopAttentionTracking();
        _session?.Kill();
        SetState(TerminalSessionState.Stopped, "Stopped by user");
        SessionExited?.Invoke();
    }

    /// <summary>Relaunches the last command in the same working directory.</summary>
    public void Restart()
    {
        if (!string.IsNullOrWhiteSpace(_commandLine))
        {
            SpawnConPty();
        }
    }

    public void Dispose()
    {
        _attentionTimer.Stop();
        Loaded -= EmbeddedTerminalControl_Loaded;
        if (WebView.CoreWebView2 != null)
        {
            try
            {
                WebView.CoreWebView2.WebMessageReceived -= CoreWebView2_WebMessageReceived;
            }
            catch { }
        }
        _session?.Dispose();
        _session = null;
    }
}
