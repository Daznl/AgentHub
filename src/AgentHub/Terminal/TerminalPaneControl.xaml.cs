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

    // Status dot colours, mirrored from the embedded terminal's session state.
    private static readonly SolidColorBrush IdleBrush = new(Color.FromRgb(0x5A, 0x6B, 0x85));
    private static readonly SolidColorBrush RunningBrush = new(Color.FromRgb(34, 197, 94));
    private static readonly SolidColorBrush StoppedBrush = new(Color.FromRgb(113, 113, 122));
    private static readonly SolidColorBrush FailedBrush = new(Color.FromRgb(239, 68, 68));

    private SolidColorBrush? _workingBorderBrush;
    private SolidColorBrush? _workingHeaderBrush;

    public event Action<TerminalPaneControl>? CloseRequested;
    public event Action<TerminalPaneControl, int>? MoveRequested;
    /// <summary>Raised when the user asks to browse for a folder to launch in. The host shows the picker and calls <see cref="SetCustomFolder"/>.</summary>
    public event Action<TerminalPaneControl>? FolderPickRequested;
    /// <summary>Raised when the user wants to add or remove SSH connections (also when SSH is pressed with none saved).</summary>
    public event Action? SshManageRequested;
    private List<SshConnection> _sshConnections = [];
    private List<RepositoryDefinition> _repositories = [];
    private LaunchLocation? _customLocation;
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
        Terminal.StatusChanged += OnTerminalStatusChanged;
        Terminal.SubtitleChanged += subtitle => SubtitleText.Text = subtitle;
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
    }

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
            PaneBorder.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
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
        // The "Terminal N" label was removed from the header; only the internal index is tracked now.
        _paneIndex = index;
        _ = accent;
    }

    /// <summary>Folder a session starts in when no repository or folder has been chosen.</summary>
    public static string BaseDirectory => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>The folder the Launch button will start in.</summary>
    public string SelectedFolder => (RepoCombo.SelectedItem as LaunchLocation)?.Path ?? BaseDirectory;

    public void UpdateRepositories(List<RepositoryDefinition> repos)
    {
        _repositories = repos;
        var prev = RepoCombo.SelectedItem as LaunchLocation;
        RebuildLocations(prev);
    }

    /// <summary>Adds (or replaces) the browsed-folder entry in the location dropdown and selects it.</summary>
    public void SetCustomFolder(string folder)
    {
        var name = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        _customLocation = new LaunchLocation
        {
            Name = "📁 " + (string.IsNullOrWhiteSpace(name) ? folder : name),
            Path = folder
        };
        RebuildLocations(_customLocation);
    }

    // Dropdown order: home (the default), the browsed folder if any, then registered repositories.
    private void RebuildLocations(LaunchLocation? select)
    {
        var home = new LaunchLocation { Name = "🏠 Home (no repo)", Path = BaseDirectory };
        var items = new List<LaunchLocation> { home };
        if (_customLocation is not null) items.Add(_customLocation);
        items.AddRange(_repositories.Select(r => new LaunchLocation { Name = r.Name, Path = r.LocalPath, Repository = r }));

        RepoCombo.ItemsSource = items;
        RepoCombo.SelectedItem =
            (select?.Repository is { } repo ? items.FirstOrDefault(i => i.Repository?.Id == repo.Id) : null)
            ?? (select is not null && select == _customLocation ? _customLocation : null)
            ?? home;
    }

    private void RepoCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RepoCombo.ToolTip = RepoCombo.SelectedItem is LaunchLocation loc
            ? $"Launches in {loc.Path}"
            : "Select working repository or folder";
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

    // Reflects the embedded terminal's session state onto the shared header (dot colour + Stop/Restart buttons).
    private void OnTerminalStatusChanged(TerminalStatus status)
    {
        switch (status)
        {
            case TerminalStatus.Running:
                StatusDot.Fill = RunningBrush;
                RestartBtn.Visibility = Visibility.Collapsed;
                StopBtn.Visibility = Visibility.Visible;
                break;
            case TerminalStatus.Stopped:
                StatusDot.Fill = StoppedBrush;
                RestartBtn.Visibility = Visibility.Visible;
                StopBtn.Visibility = Visibility.Collapsed;
                break;
            case TerminalStatus.Failed:
                StatusDot.Fill = FailedBrush;
                RestartBtn.Visibility = Visibility.Visible;
                StopBtn.Visibility = Visibility.Collapsed;
                break;
            default:
                StatusDot.Fill = IdleBrush;
                RestartBtn.Visibility = Visibility.Collapsed;
                StopBtn.Visibility = Visibility.Collapsed;
                break;
        }
    }

    private void StopBtn_Click(object sender, RoutedEventArgs e) => Terminal.StopSession();

    private void RestartBtn_Click(object sender, RoutedEventArgs e) => Terminal.RestartSession();

    private void Launch_Click(object sender, RoutedEventArgs e)
    {
        var location = RepoCombo.SelectedItem as LaunchLocation;
        var dir = location?.Path ?? BaseDirectory;
        if (!Directory.Exists(dir))
        {
            System.Windows.MessageBox.Show(Window.GetWindow(this), $"The folder no longer exists:\n{dir}", "AgentHub",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var repoName = location?.Repository?.Name ?? location?.Name ?? "Home";
        var shell = ShellCombo.SelectedItem as ShellOption;
        var cmd = shell?.Command ?? "powershell.exe -NoLogo";
        var title = shell?.DisplayName ?? "Shell";

        if (Terminal.IsRunning)
        {
            var replace = System.Windows.MessageBox.Show(Window.GetWindow(this),
                $"This terminal has a running session. Replace it with {title} in {dir}?",
                "Replace session", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (replace != MessageBoxResult.Yes) return;
        }

        Terminal.StartSession(cmd, dir, $"Terminal {_paneIndex} · {title}", repoName, shell?.IsAgent ?? false);
    }

    public void UpdateSshConnections(List<SshConnection> connections)
    {
        _sshConnections = connections;
        SshList.ItemsSource = null;
        SshList.ItemsSource = connections;
        SshBtn.ToolTip = connections.Count switch
        {
            0 => "Add an SSH connection",
            1 => $"SSH to {connections[0].DisplayName}",
            _ => "Choose a saved SSH connection"
        };
    }

    // One saved connection: connect straight away. Several: show the picker. None: open the manager.
    private void Ssh_Click(object sender, RoutedEventArgs e)
    {
        switch (_sshConnections.Count)
        {
            case 0:
                SshManageRequested?.Invoke();
                break;
            case 1:
                ConnectSsh(_sshConnections[0]);
                break;
            default:
                SshPopup.IsOpen = true;
                break;
        }
    }

    private void SshItem_Click(object sender, RoutedEventArgs e)
    {
        SshPopup.IsOpen = false;
        if ((sender as FrameworkElement)?.Tag is SshConnection connection)
            ConnectSsh(connection);
    }

    private void SshManage_Click(object sender, RoutedEventArgs e)
    {
        SshPopup.IsOpen = false;
        SshManageRequested?.Invoke();
    }

    private void ConnectSsh(SshConnection connection)
    {
        if (Terminal.IsRunning)
        {
            var replace = System.Windows.MessageBox.Show(Window.GetWindow(this),
                $"This terminal has a running session. Replace it with SSH to {connection.DisplayName}?",
                "Replace session", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (replace != MessageBoxResult.Yes) return;
        }

        Terminal.StartSession(connection.BuildCommandLine(), BaseDirectory, $"Terminal {_paneIndex} · SSH", $"SSH · {connection.DisplayName}");
    }

    private void BrowseFolder_Click(object sender, RoutedEventArgs e) => FolderPickRequested?.Invoke(this);

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        CloseRequested?.Invoke(this);
    }

    private void MoveLeft_Click(object sender, RoutedEventArgs e) => MoveRequested?.Invoke(this, -1);

    private void MoveRight_Click(object sender, RoutedEventArgs e) => MoveRequested?.Invoke(this, 1);

    public void StartSession(string commandLine, string workingDirectory, string title, string repoName, bool isAgentSession = false)
    {
        // Keep the location dropdown in step with where the session actually runs.
        if (!string.IsNullOrEmpty(workingDirectory) && RepoCombo.ItemsSource is IEnumerable<LaunchLocation> locations)
        {
            var match = locations.FirstOrDefault(l => string.Equals(l.Path, workingDirectory, StringComparison.OrdinalIgnoreCase));
            if (match != null)
                RepoCombo.SelectedItem = match;
            else if (Directory.Exists(workingDirectory))
                SetCustomFolder(workingDirectory);
        }
        Terminal.StartSession(commandLine, workingDirectory, title, repoName, isAgentSession);
    }

    public void Dispose()
    {
        Terminal.Dispose();
    }
}

/// <summary>An entry in a pane's location dropdown: the home folder, a browsed folder, or a registered repository.</summary>
public sealed class LaunchLocation
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public RepositoryDefinition? Repository { get; init; }
}
