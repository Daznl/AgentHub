using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Color = System.Windows.Media.Color;
using UserControl = System.Windows.Controls.UserControl;

namespace AgentHub.Terminal;

public partial class EmbeddedTerminalControl : UserControl, IDisposable
{
    private static readonly SolidColorBrush RunningBrush = new(Color.FromRgb(34, 197, 94));
    private static readonly SolidColorBrush StoppedBrush = new(Color.FromRgb(113, 113, 122));
    private static readonly SolidColorBrush FailedBrush = new(Color.FromRgb(239, 68, 68));

    private ConPtySession? _session;
    private bool _isTerminalReady;
    private bool _pendingStart;
    private string _commandLine = string.Empty;
    private string _workingDirectory = string.Empty;
    private int _cols = 100;
    private int _rows = 30;

    public event Action? SessionExited;

    public EmbeddedTerminalControl()
    {
        InitializeComponent();
        Loaded += EmbeddedTerminalControl_Loaded;
        Unloaded += EmbeddedTerminalControl_Unloaded;
    }

    private async void EmbeddedTerminalControl_Loaded(object sender, RoutedEventArgs e)
    {
        if (WebView.CoreWebView2 != null) return;

        try
        {
            var userData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentHub", "WebView2");
            Directory.CreateDirectory(userData);
            var env = await CoreWebView2Environment.CreateAsync(null, userData);
            await WebView.EnsureCoreWebView2Async(env);

            if (WebView.CoreWebView2 == null) return;

            WebView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            WebView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            WebView.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;

            var htmlPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Terminal", "Assets", "terminal.html");
            if (File.Exists(htmlPath))
            {
                WebView.CoreWebView2.Navigate(new Uri(htmlPath).AbsoluteUri);
            }
        }
        catch (Exception ex)
        {
            SubtitleText.Text = "Failed to initialize terminal: " + ex.Message;
        }
    }

    private void EmbeddedTerminalControl_Unloaded(object sender, RoutedEventArgs e)
    {
        // Keep session running unless explicitly stopped
    }

    public void SetHeader(string title, string subtitle = "")
    {
        TitleText.Text = title;
        SubtitleText.Text = subtitle;
    }

    public void StartSession(string commandLine, string workingDirectory, string title = "", string subtitle = "")
    {
        _commandLine = commandLine;
        _workingDirectory = workingDirectory;
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

        try
        {
            StatusDot.Fill = RunningBrush;
            RestartBtn.Visibility = Visibility.Collapsed;
            StopBtn.Visibility = Visibility.Visible;

            _session = ConPtySession.Start(_commandLine, _workingDirectory, _cols, _rows);
            _session.OutputDataReceived += text =>
            {
                Dispatcher.InvokeAsync(() =>
                {
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
                    StatusDot.Fill = exitCode == 0 ? StoppedBrush : FailedBrush;
                    SubtitleText.Text = $"Exited (code {exitCode})";
                    RestartBtn.Visibility = Visibility.Visible;
                    StopBtn.Visibility = Visibility.Collapsed;
                    SessionExited?.Invoke();
                });
            };

            SubtitleText.Text = $"PID {_session.ProcessId} · {_workingDirectory}";
        }
        catch (Exception ex)
        {
            StatusDot.Fill = FailedBrush;
            SubtitleText.Text = "Launch failed: " + ex.Message;
            RestartBtn.Visibility = Visibility.Visible;
            StopBtn.Visibility = Visibility.Collapsed;
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
                        if (data != null) _session?.Write(data);
                    }
                    break;
            }
        }
        catch { }
    }

    private void StopBtn_Click(object sender, RoutedEventArgs e)
    {
        _session?.Kill();
        StatusDot.Fill = StoppedBrush;
        SubtitleText.Text = "Stopped by user";
        RestartBtn.Visibility = Visibility.Visible;
        StopBtn.Visibility = Visibility.Collapsed;
    }

    private void RestartBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_commandLine))
        {
            SpawnConPty();
        }
    }

    public void Dispose()
    {
        _session?.Dispose();
        _session = null;
    }
}
