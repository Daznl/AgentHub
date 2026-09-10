using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using AgentHub.Models;
using Color = System.Windows.Media.Color;
using UserControl = System.Windows.Controls.UserControl;

namespace AgentHub.Terminal;

public partial class TerminalPaneControl : UserControl, IDisposable
{
    private static readonly Color AttentionColor = Color.FromRgb(0xF5, 0x9E, 0x0B);       // amber border
    private static readonly Color AttentionHeaderColor = Color.FromRgb(0x3B, 0x2A, 0x12); // amber-tinted header
    private static readonly Color HeaderBaseColor = Color.FromRgb(0x0E, 0x16, 0x22);
    private static readonly Color WorkingColor = Color.FromRgb(0x3A, 0x7B, 0xD5);        // soft blue border while the agent works
    private static readonly Color WorkingHeaderColor = Color.FromRgb(0x11, 0x24, 0x3E);  // blue-tinted header

    private SolidColorBrush? _workingBorderBrush;
    private SolidColorBrush? _workingHeaderBrush;

    public event Action<TerminalPaneControl>? CloseRequested;
    public event Action<TerminalPaneControl, int>? MoveRequested;
    /// <summary>Raised when the user asks to pick an arbitrary folder and launch the selected shell there.</summary>
    public event Action<TerminalPaneControl>? FolderLaunchRequested;
    /// <summary>Raised once when the coding CLI in this pane stops working and waits for the user.</summary>
    public event Action<TerminalPaneControl>? AttentionRequested;
    private int _paneIndex = 1;

    public EmbeddedTerminalControl TerminalControl => Terminal;
    public int PaneIndex => _paneIndex;
    public bool IsRunning => Terminal.IsRunning;
    public ShellOption? SelectedShell => ShellCombo.SelectedItem as ShellOption;
    public event Action? SessionStateChanged;

    public TerminalPaneControl()
    {
        InitializeComponent();
        Terminal.SessionStarted += () => SessionStateChanged?.Invoke();
        Terminal.SessionExited += () => SessionStateChanged?.Invoke();
        Terminal.AttentionRequested += () =>
        {
            ShowAttention();
            AttentionRequested?.Invoke(this);
        };
        Terminal.AttentionCleared += ClearAttention;
        Terminal.WorkingStateChanged += working =>
        {
            if (working) StartWorkingPulse();
            else StopWorkingPulse();
        };
        Terminal.StatusChanged += UpdateSessionStatus;
        UpdateSessionStatus();
    }

    private static readonly SolidColorBrush IdleDotBrush = new(Color.FromRgb(0x5A, 0x6B, 0x85));
    private static readonly SolidColorBrush RunningDotBrush = new(Color.FromRgb(34, 197, 94));
    private static readonly SolidColorBrush StoppedDotBrush = new(Color.FromRgb(113, 113, 122));
    private static readonly SolidColorBrush FailedDotBrush = new(Color.FromRgb(239, 68, 68));

    // Renders the terminal's session state in the pane's single header row.
    private void UpdateSessionStatus()
    {
        StatusDot.Fill = Terminal.State switch
        {
            TerminalSessionState.Running => RunningDotBrush,
            TerminalSessionState.Failed => FailedDotBrush,
            TerminalSessionState.Stopped => StoppedDotBrush,
            _ => IdleDotBrush
        };

        // Launchers pass "Terminal N · <shell>"; the pane title already shows "Terminal N".
        var label = Terminal.SessionLabel;
        var prefix = PaneTitleText.Text + " · ";
        if (label.StartsWith(prefix, StringComparison.Ordinal)) label = label[prefix.Length..];
        SessionLabelText.Text = string.IsNullOrWhiteSpace(label) ? "" : "· " + label;

        PaneTitleGroup.ToolTip = string.IsNullOrWhiteSpace(Terminal.StatusDetail) ? null : Terminal.StatusDetail;
        StopBtn.Visibility = Terminal.State == TerminalSessionState.Running ? Visibility.Visible : Visibility.Collapsed;
        RestartBtn.Visibility = Terminal.CanRestart ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => Terminal.Stop();

    private void Restart_Click(object sender, RoutedEventArgs e) => Terminal.Restart();

    // Gentle blue breathing on the border and header while the agent is producing output.
    private void StartWorkingPulse()
    {
        if (_workingBorderBrush is not null) return;

        var baseBorder = ((SolidColorBrush)FindResource("BorderBrush")).Color;
        _workingBorderBrush = new SolidColorBrush(baseBorder);
        _workingHeaderBrush = new SolidColorBrush(HeaderBaseColor);
        PaneBorder.BorderBrush = _workingBorderBrush;
        PaneHeader.Background = _workingHeaderBrush;

        _workingBorderBrush.BeginAnimation(SolidColorBrush.ColorProperty, BuildBreath(baseBorder, WorkingColor));
        _workingHeaderBrush.BeginAnimation(SolidColorBrush.ColorProperty, BuildBreath(HeaderBaseColor, WorkingHeaderColor));
    }

    private void StopWorkingPulse()
    {
        if (_workingBorderBrush is null) return;

        _workingBorderBrush.BeginAnimation(SolidColorBrush.ColorProperty, null);
        _workingHeaderBrush?.BeginAnimation(SolidColorBrush.ColorProperty, null);
        _workingBorderBrush = null;
        _workingHeaderBrush = null;
        PaneBorder.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        PaneHeader.Background = new SolidColorBrush(HeaderBaseColor);
    }

    private static ColorAnimation BuildBreath(Color from, Color to) => new()
    {
        From = from,
        To = to,
        Duration = TimeSpan.FromSeconds(1.6),
        AutoReverse = true,
        RepeatBehavior = RepeatBehavior.Forever,
        EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
    };

    /// <summary>Applies the user's attention settings (flash on/off and idle threshold in seconds).</summary>
    public void ConfigureAttention(bool enabled, double idleSeconds)
    {
        Terminal.AttentionEnabled = enabled;
        Terminal.AttentionIdleSeconds = idleSeconds;
    }

    // Three amber pulses on the pane border and header, then a persistent badge until the user types.
    private void ShowAttention()
    {
        StopWorkingPulse();
        AttentionBadge.Visibility = Visibility.Visible;

        var baseBorder = ((SolidColorBrush)FindResource("BorderBrush")).Color;
        var borderBrush = new SolidColorBrush(baseBorder);
        var headerBrush = new SolidColorBrush(HeaderBaseColor);
        PaneBorder.BorderBrush = borderBrush;
        PaneHeader.Background = headerBrush;

        borderBrush.BeginAnimation(SolidColorBrush.ColorProperty, BuildPulse(baseBorder, AttentionColor, restore: () =>
        {
            // If the working pulse took over the border while the amber flash was still running,
            // leave its brush in place; otherwise the pane would look inert until the next turn.
            if (ReferenceEquals(PaneBorder.BorderBrush, borderBrush))
            {
                PaneBorder.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
            }
        }));
        headerBrush.BeginAnimation(SolidColorBrush.ColorProperty, BuildPulse(HeaderBaseColor, AttentionHeaderColor, restore: null));
    }

    private static ColorAnimationUsingKeyFrames BuildPulse(Color from, Color to, Action? restore)
    {
        const int pulses = 3;
        const double pulseSeconds = 0.8;
        var anim = new ColorAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromSeconds(pulses * pulseSeconds),
            FillBehavior = FillBehavior.Stop
        };
        for (var i = 0; i < pulses; i++)
        {
            anim.KeyFrames.Add(new EasingColorKeyFrame(to, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(i * pulseSeconds + pulseSeconds / 2))));
            anim.KeyFrames.Add(new EasingColorKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.FromSeconds((i + 1) * pulseSeconds))));
        }
        if (restore is not null)
        {
            anim.Completed += (_, _) => restore();
        }
        return anim;
    }

    private void ClearAttention()
    {
        AttentionBadge.Visibility = Visibility.Collapsed;
    }

    public void SetIndex(int index, SolidColorBrush accent)
    {
        _paneIndex = index;
        PaneTitleText.Text = $"Terminal {index}";
        PaneTitleText.Foreground = accent;
    }

    public void UpdateRepositories(List<RepositoryDefinition> repos, int defaultIndex = 0)
    {
        var prevSelected = RepoCombo.SelectedItem as RepositoryDefinition;
        RepoCombo.ItemsSource = null;
        RepoCombo.ItemsSource = repos;
        if (prevSelected != null && repos.Any(r => r.Id == prevSelected.Id))
        {
            RepoCombo.SelectedItem = repos.First(r => r.Id == prevSelected.Id);
        }
        else if (repos.Count > 0)
        {
            var idx = Math.Clamp(defaultIndex, 0, repos.Count - 1);
            RepoCombo.SelectedIndex = idx;
        }
    }

    public void UpdateShellOptions(List<ShellOption> shells, int defaultIndex = 0)
    {
        var prevSelected = ShellCombo.SelectedItem as ShellOption;
        ShellCombo.ItemsSource = null;
        ShellCombo.ItemsSource = shells;
        if (prevSelected != null && shells.Any(s => s.DisplayName == prevSelected.DisplayName))
        {
            ShellCombo.SelectedItem = shells.First(s => s.DisplayName == prevSelected.DisplayName);
        }
        else if (shells.Count > 0)
        {
            var idx = Math.Clamp(defaultIndex, 0, shells.Count - 1);
            ShellCombo.SelectedIndex = idx;
        }
    }

    private void Launch_Click(object sender, RoutedEventArgs e)
    {
        var repo = RepoCombo.SelectedItem as RepositoryDefinition;
        var dir = repo?.LocalPath ?? Directory.GetCurrentDirectory();
        var repoName = repo?.Name ?? "Workspace";
        var shell = ShellCombo.SelectedItem as ShellOption;
        var cmd = shell?.Command ?? "powershell.exe -NoLogo";
        var title = shell?.DisplayName ?? "Shell";

        Terminal.StartSession(cmd, dir, $"Terminal {_paneIndex} · {title}", repoName, shell?.IsAgent ?? false);
    }

    private void LaunchInFolder_Click(object sender, RoutedEventArgs e) => FolderLaunchRequested?.Invoke(this);

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        CloseRequested?.Invoke(this);
    }

    private void MoveLeft_Click(object sender, RoutedEventArgs e) => MoveRequested?.Invoke(this, -1);

    private void MoveRight_Click(object sender, RoutedEventArgs e) => MoveRequested?.Invoke(this, 1);

    public void StartSession(string commandLine, string workingDirectory, string title, string repoName, bool isAgentSession = false)
    {
        if (!string.IsNullOrEmpty(workingDirectory) && RepoCombo.ItemsSource is IEnumerable<RepositoryDefinition> repos)
        {
            var match = repos.FirstOrDefault(r => string.Equals(r.LocalPath, workingDirectory, StringComparison.OrdinalIgnoreCase));
            if (match != null)
            {
                RepoCombo.SelectedItem = match;
            }
        }
        Terminal.StartSession(commandLine, workingDirectory, title, repoName, isAgentSession);
    }

    public void Dispose()
    {
        Terminal.Dispose();
    }
}
