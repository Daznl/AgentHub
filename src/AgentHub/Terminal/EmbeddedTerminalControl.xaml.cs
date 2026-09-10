using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
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

    private ConPtySession? _session;
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
        _session?.Dispose();
        _session = null;

        try
        {
            RaiseStatus(TerminalStatus.Running);

            try
            {
                WebView.CoreWebView2?.PostWebMessageAsString(JsonSerializer.Serialize(new { type = "clear" }));
            }
            catch { }

            _session = ConPtySession.Start(_commandLine, _workingDirectory, _cols, _rows);

            _attention.OnSessionStarted(DateTimeOffset.Now);
            _attentionTimer.IsEnabled = IsAgentSession && AttentionEnabled;

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
                    RaiseStatus(exitCode == 0 ? TerminalStatus.Stopped : TerminalStatus.Failed);
                    RaiseSubtitle($"Exited (code {exitCode})");
                    SessionExited?.Invoke();
                });
            };

            RaiseSubtitle($"PID {_session.ProcessId} · {_workingDirectory}");
            SessionStarted?.Invoke();

            // A freshly-launched TUI (e.g. Claude Code) reads the terminal size at
            // startup. Force a couple of re-fits so it gets the true current size and
            // its bottom input/mode line isn't rendered below the visible area.
            ScheduleRefit();
        }
        catch (Exception ex)
        {
            RaiseStatus(TerminalStatus.Failed);
            RaiseSubtitle("Launch failed: " + ex.Message);
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

    /// <summary>Kills the running session. Called by the host pane's Stop button.</summary>
    public void StopSession()
    {
        StopAttentionTracking();
        _session?.Kill();
        RaiseStatus(TerminalStatus.Stopped);
        RaiseSubtitle("Stopped by user");
        SessionExited?.Invoke();
    }

    /// <summary>Re-launches the last command. Called by the host pane's Restart button.</summary>
    public void RestartSession()
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
