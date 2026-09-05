using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AgentHub.Models;
using UserControl = System.Windows.Controls.UserControl;

namespace AgentHub.Terminal;

public partial class TerminalPaneControl : UserControl, IDisposable
{
    public event Action<TerminalPaneControl>? CloseRequested;
    public event Action<TerminalPaneControl, int>? MoveRequested;
    private int _paneIndex = 1;

    public EmbeddedTerminalControl TerminalControl => Terminal;
    public int PaneIndex => _paneIndex;
    public bool IsRunning => Terminal.IsRunning;
    public event Action? SessionStateChanged;

    public TerminalPaneControl()
    {
        InitializeComponent();
        Terminal.SessionStarted += () => SessionStateChanged?.Invoke();
        Terminal.SessionExited += () => SessionStateChanged?.Invoke();
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

        Terminal.StartSession(cmd, dir, $"Terminal {_paneIndex} · {title}", repoName);
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        CloseRequested?.Invoke(this);
    }

    private void MoveLeft_Click(object sender, RoutedEventArgs e) => MoveRequested?.Invoke(this, -1);

    private void MoveRight_Click(object sender, RoutedEventArgs e) => MoveRequested?.Invoke(this, 1);

    public void StartSession(string commandLine, string workingDirectory, string title, string repoName)
    {
        if (!string.IsNullOrEmpty(workingDirectory) && RepoCombo.ItemsSource is IEnumerable<RepositoryDefinition> repos)
        {
            var match = repos.FirstOrDefault(r => string.Equals(r.LocalPath, workingDirectory, StringComparison.OrdinalIgnoreCase));
            if (match != null)
            {
                RepoCombo.SelectedItem = match;
            }
        }
        Terminal.StartSession(commandLine, workingDirectory, title, repoName);
    }

    public void Dispose()
    {
        Terminal.Dispose();
    }
}
