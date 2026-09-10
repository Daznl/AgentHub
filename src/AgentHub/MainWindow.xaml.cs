using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using AgentHub.Models;
using AgentHub.Services;
using AgentHub.Services.Usage;
using AgentHub.Terminal;
using Color = System.Windows.Media.Color;
using Forms = System.Windows.Forms;
using MessageBox = System.Windows.MessageBox;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using Button = System.Windows.Controls.Button;
using Clipboard = System.Windows.Clipboard;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace AgentHub;

public sealed class ShellOption
{
    public required string DisplayName { get; init; }
    public required string Command { get; init; }
    public override string ToString() => DisplayName;
}

public partial class MainWindow : Window
{
    private readonly SettingsService _settingsService = new();
    private readonly ProcessRunner _runner = new();
    private readonly GitService _git;
    private readonly AgentLauncher _agentLauncher = new();
    private readonly RepoContextService _repoContext = new();
    private readonly GitHubService _githubService;
    private readonly RepoDiscoveryService _discovery;
    private readonly UsageService _usageService;
    private readonly DispatcherTimer _usageRefreshTimer = new();
    private readonly List<ShellOption> _shellOptions = new();
    private readonly List<TerminalPaneControl> _panes = new();
    private static readonly SolidColorBrush[] PaneAccentBrushes =
    [
        new(Color.FromRgb(96, 165, 250)),  // Blue (Terminal 1)
        new(Color.FromRgb(192, 132, 252)), // Purple (Terminal 2)
        new(Color.FromRgb(52, 211, 153)),  // Emerald (Terminal 3)
        new(Color.FromRgb(251, 191, 36)),  // Amber (Terminal 4)
        new(Color.FromRgb(56, 189, 248)),  // Sky (Terminal 5)
        new(Color.FromRgb(244, 114, 182))  // Pink (Terminal 6)
    ];

    private List<GitHubRepository> _allGitHubRepos = new();
    private List<GitHubAccount> _gitHubAccounts = new();
    private bool _suppressAccountSelection;
    private CancellationTokenSource? _gitHubLoadCts;
    private bool _gitHubLoading;
    private AppSettings _settings = new();
    private RepositoryDefinition? _selectedRepo;
    private readonly Dictionary<string, UsageSnapshot> _lastSuccessfulUsage =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, UsageSnapshot> _latestSnapshots =
        new(StringComparer.OrdinalIgnoreCase);
    private string _selectedFeedProviderId = "gemini";
    private bool _showRawAnsi;
    private DateTimeOffset? _lastUsageRefresh;
    private bool _usageRefreshInProgress;

    public MainWindow()
    {
        InitializeComponent();
        _git = new GitService(_runner);
        _githubService = new GitHubService(_runner);
        _discovery = new RepoDiscoveryService(_git);
        _usageService = new UsageService(_runner);
        _usageRefreshTimer.Tick += async (_, _) => await RefreshUsageAsync();
        Loaded += MainWindow_Loaded;
        AgentCombo.SelectionChanged += (_, _) => UpdateAgentPreview();
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Activate();
        _settings = await _settingsService.LoadAsync();

        if (_settings.Repositories.Count == 0)
        {
            await AutoDiscoverInitialReposAsync();
        }

        PopulateShellOptions();
        InitDefaultTerminalPanes(1);
        RefreshRepoList();
        RefreshAgentList();
        ViewCockpitBtn_Click(this, new RoutedEventArgs());
        StatusText.Text = $"Settings: {_settingsService.SettingsPath}";

        UsageIntervalBox.Text = _settings.UsageRefreshMinutes.ToString("0.##", CultureInfo.InvariantCulture);
        UpdateUsagePollingInterval();
        _usageRefreshTimer.Start();
        _ = RefreshUsageAsync();

        _ = RefreshAllRepoSnapshotsAsync(fetchRemotes: false);
        _ = LoadGitHubReposAsync();
    }

    private async Task AutoDiscoverInitialReposAsync()
    {
        try
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            var discovered = await _discovery.ScanFolderAsync(desktop, maxDepth: 1);
            foreach (var d in discovered)
            {
                if (!_settings.Repositories.Any(r => string.Equals(r.LocalPath, d.LocalPath, StringComparison.OrdinalIgnoreCase)))
                {
                    _settings.Repositories.Add(new RepositoryDefinition
                    {
                        Name = d.Name,
                        LocalPath = d.LocalPath,
                        RemoteUrl = d.RemoteUrl,
                        CurrentBranch = d.Snapshot?.Branch,
                        Ahead = d.Snapshot?.Ahead ?? 0,
                        Behind = d.Snapshot?.Behind ?? 0,
                        ChangedFiles = d.Snapshot?.ChangedFiles ?? 0
                    });
                }
            }
            if (_settings.Repositories.Count > 0)
            {
                await _settingsService.SaveAsync(_settings);
            }
        }
        catch { }
    }

    private void RefreshRepoList()
    {
        var selectedId = _selectedRepo?.Id;
        var repos = _settings.Repositories.OrderBy(r => r.Name).ToList();

        RepoList.ItemsSource = null;
        RepoList.ItemsSource = repos;
        if (selectedId is not null)
            RepoList.SelectedItem = repos.FirstOrDefault(r => r.Id == selectedId);

        for (int i = 0; i < _panes.Count; i++)
        {
            _panes[i].UpdateRepositories(repos, i);
        }

        UpdateGitHubAddedState();
        ApplyGitHubFilter();
    }

    private void RefreshAgentList()
    {
        AgentCombo.ItemsSource = _settings.Agents.Where(a => a.Enabled).ToList();
        if (AgentCombo.Items.Count > 0) AgentCombo.SelectedIndex = 0;
        PopulateShellOptions();
    }

    private void PopulateShellOptions()
    {
        _shellOptions.Clear();
        _shellOptions.Add(new ShellOption { DisplayName = "PowerShell", Command = "powershell.exe -NoLogo" });
        _shellOptions.Add(new ShellOption { DisplayName = "Command Prompt", Command = "cmd.exe" });
        foreach (var agent in _settings.Agents.Where(a => a.Enabled))
        {
            var cmd = string.IsNullOrWhiteSpace(agent.Arguments) ? agent.Command : $"{agent.Command} {agent.Arguments}";
            _shellOptions.Add(new ShellOption { DisplayName = agent.Name, Command = cmd });
        }

        for (int i = 0; i < _panes.Count; i++)
        {
            _panes[i].UpdateShellOptions(_shellOptions, i);
        }
    }

    private TerminalPaneControl CreateTerminalPane(int defaultRepoIndex = 0, int defaultShellIndex = 0)
    {
        var pane = new TerminalPaneControl();
        pane.CloseRequested += OnPaneCloseRequested;
        pane.MoveRequested += OnPaneMoveRequested;
        pane.FolderLaunchRequested += OnPaneFolderLaunchRequested;
        pane.SessionStateChanged += UpdateUsagePollingInterval;
        pane.UpdateRepositories(_settings.Repositories, defaultRepoIndex);
        pane.UpdateShellOptions(_shellOptions, defaultShellIndex);
        return pane;
    }

    private void InitDefaultTerminalPanes(int count = 3)
    {
        foreach (var p in _panes) p.Dispose();
        _panes.Clear();

        for (int i = 0; i < count; i++)
        {
            _panes.Add(CreateTerminalPane(defaultRepoIndex: i, defaultShellIndex: 0));
        }

        RebuildTerminalLayout();
    }

    private void RebuildTerminalLayout()
    {
        TerminalsContainerGrid.Children.Clear();
        TerminalsContainerGrid.ColumnDefinitions.Clear();

        if (_panes.Count == 0) return;

        for (int i = 0; i < _panes.Count; i++)
        {
            var pane = _panes[i];
            var accent = PaneAccentBrushes[i % PaneAccentBrushes.Length];
            pane.SetIndex(i + 1, accent);

            TerminalsContainerGrid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star),
                MinWidth = 200
            });

            Grid.SetColumn(pane, TerminalsContainerGrid.ColumnDefinitions.Count - 1);
            TerminalsContainerGrid.Children.Add(pane);

            if (i < _panes.Count - 1)
            {
                TerminalsContainerGrid.ColumnDefinitions.Add(new ColumnDefinition
                {
                    Width = new GridLength(6, GridUnitType.Pixel)
                });

                var splitter = new GridSplitter
                {
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
                    Background = new SolidColorBrush(Color.FromRgb(39, 39, 42)),
                    Cursor = System.Windows.Input.Cursors.SizeWE
                };

                Grid.SetColumn(splitter, TerminalsContainerGrid.ColumnDefinitions.Count - 1);
                TerminalsContainerGrid.Children.Add(splitter);
            }
        }

        if (TerminalCountBadge != null)
            TerminalCountBadge.Text = $"{_panes.Count} SESSION{(_panes.Count == 1 ? "" : "S")}";
    }

    private void AddTerminal_Click(object sender, RoutedEventArgs e)
    {
        if (_panes.Count >= 6)
        {
            MessageBox.Show("Maximum of 6 side-by-side terminal sessions supported.", "AgentHub", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var newPane = CreateTerminalPane(defaultRepoIndex: _panes.Count, defaultShellIndex: 0);
        _panes.Add(newPane);
        RebuildTerminalLayout();
        UpdateUsagePollingInterval();
        StatusText.Text = $"Added Terminal {_panes.Count}.";
    }

    private const string PlainPowerShellCommand = "powershell.exe -NoLogo";

    /// <summary>
    /// Shows the Windows folder picker, starting at the last folder the user chose (or the Desktop).
    /// Returns null when cancelled. Remembers the choice in settings for next time.
    /// </summary>
    private async Task<string?> PickWorkingFolderAsync(string description)
    {
        var start = !string.IsNullOrWhiteSpace(_settings.LastTerminalFolder) && Directory.Exists(_settings.LastTerminalFolder)
            ? _settings.LastTerminalFolder
            : Environment.GetFolderPath(Environment.SpecialFolder.Desktop);

        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = description,
            UseDescriptionForTitle = true,
            SelectedPath = start,
            ShowNewFolderButton = true
        };
        if (dialog.ShowDialog() != Forms.DialogResult.OK || string.IsNullOrWhiteSpace(dialog.SelectedPath))
            return null;

        var folder = dialog.SelectedPath;
        if (!Directory.Exists(folder))
        {
            MessageBox.Show(this, $"The folder no longer exists:\n{folder}", "AgentHub", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }

        if (!string.Equals(_settings.LastTerminalFolder, folder, StringComparison.OrdinalIgnoreCase))
        {
            _settings.LastTerminalFolder = folder;
            try { await _settingsService.SaveAsync(_settings); } catch { /* remembering the folder is best-effort */ }
        }

        return folder;
    }

    private static string FolderDisplayName(string folder)
    {
        var name = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return string.IsNullOrWhiteSpace(name) ? folder : name;
    }

    /// <summary>
    /// Cockpit toolbar: browse for any folder and open a plain PowerShell there. Uses a new pane when
    /// there is room, otherwise the first idle pane, so a running agent session is never replaced silently.
    /// </summary>
    private async void OpenPowerShellInFolder_Click(object sender, RoutedEventArgs e)
    {
        var folder = await PickWorkingFolderAsync("Choose the folder to open PowerShell in");
        if (folder is null) return;

        TerminalPaneControl? target;
        if (_panes.Count < 6)
        {
            target = CreateTerminalPane(defaultRepoIndex: _panes.Count, defaultShellIndex: 0);
            _panes.Add(target);
            RebuildTerminalLayout();
            UpdateUsagePollingInterval();
        }
        else
        {
            target = _panes.FirstOrDefault(p => !p.IsRunning);
            if (target is null)
            {
                MessageBox.Show(this,
                    "All 6 terminal panes are busy. Close or finish a session first, or use the 📂 button on a pane to replace that session.",
                    "AgentHub", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
        }

        var name = FolderDisplayName(folder);
        target.StartSession(PlainPowerShellCommand, folder, $"Terminal {target.PaneIndex} · PowerShell", name);
        StatusText.Text = $"Opened PowerShell in {folder} (Terminal {target.PaneIndex}).";
    }

    /// <summary>Per-pane 📂 button: browse for a folder and launch that pane's selected shell/agent there.</summary>
    private async void OnPaneFolderLaunchRequested(TerminalPaneControl pane)
    {
        var shell = pane.SelectedShell;
        var shellName = shell?.DisplayName ?? "PowerShell";

        if (pane.IsRunning)
        {
            var replace = MessageBox.Show(this,
                $"Terminal {pane.PaneIndex} has a running session. Replace it with {shellName} in a folder you choose?",
                "Replace session", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (replace != MessageBoxResult.Yes) return;
        }

        var folder = await PickWorkingFolderAsync($"Choose the folder to launch {shellName} in (Terminal {pane.PaneIndex})");
        if (folder is null) return;

        var name = FolderDisplayName(folder);
        pane.StartSession(shell?.Command ?? PlainPowerShellCommand, folder, $"Terminal {pane.PaneIndex} · {shellName}", name);
        StatusText.Text = $"Launched {shellName} in {folder} (Terminal {pane.PaneIndex}).";
    }

    private void OnPaneCloseRequested(TerminalPaneControl pane)
    {
        if (_panes.Count <= 1)
        {
            MessageBox.Show("At least one terminal session must remain open in the Cockpit.", "AgentHub", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        pane.Dispose();
        _panes.Remove(pane);
        RebuildTerminalLayout();
        UpdateUsagePollingInterval();
        StatusText.Text = $"Terminal closed. {_panes.Count} session(s) active.";
    }

    private void OnPaneMoveRequested(TerminalPaneControl pane, int offset)
    {
        var currentIndex = _panes.IndexOf(pane);
        var targetIndex = currentIndex + offset;
        if (currentIndex < 0 || targetIndex < 0 || targetIndex >= _panes.Count) return;

        (_panes[currentIndex], _panes[targetIndex]) = (_panes[targetIndex], _panes[currentIndex]);
        RebuildTerminalLayout();
        StatusText.Text = $"Moved terminal to position {targetIndex + 1}.";
    }

    private async void RepoList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedRepo = RepoList.SelectedItem as RepositoryDefinition;
        if (_selectedRepo is null)
        {
            RepoDetail.Visibility = Visibility.Collapsed;
            return;
        }

        RepoDetail.Visibility = Visibility.Visible;
        RepoNameText.Text = _selectedRepo.Name;
        RepoPathText.Text = _selectedRepo.LocalPath;
        await RefreshGitAsync();
    }

    private async void ScanFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "Select a folder to scan for Git repositories (e.g. Desktop, Projects)",
            UseDescriptionForTitle = true,
            SelectedPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            ShowNewFolderButton = false
        };
        if (dialog.ShowDialog() != Forms.DialogResult.OK) return;

        StatusText.Text = $"Scanning {dialog.SelectedPath} for Git repositories…";
        var discovered = await _discovery.ScanFolderAsync(dialog.SelectedPath, maxDepth: 2);

        int addedCount = 0;
        foreach (var d in discovered)
        {
            if (!_settings.Repositories.Any(r => string.Equals(r.LocalPath, d.LocalPath, StringComparison.OrdinalIgnoreCase)))
            {
                _settings.Repositories.Add(new RepositoryDefinition
                {
                    Name = d.Name,
                    LocalPath = d.LocalPath,
                    RemoteUrl = d.RemoteUrl,
                    CurrentBranch = d.Snapshot?.Branch,
                    Ahead = d.Snapshot?.Ahead ?? 0,
                    Behind = d.Snapshot?.Behind ?? 0,
                    ChangedFiles = d.Snapshot?.ChangedFiles ?? 0
                });
                addedCount++;
            }
        }

        if (addedCount > 0)
        {
            await _settingsService.SaveAsync(_settings);
            RefreshRepoList();
            await RefreshAllRepoSnapshotsAsync();
            MessageBox.Show($"Scanned folder:\n{dialog.SelectedPath}\n\nFound {discovered.Count} repository(ies).\nAdded {addedCount} new repository(ies) to AgentHub.", "Scan Complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            MessageBox.Show($"Scanned folder:\n{dialog.SelectedPath}\n\nFound {discovered.Count} repository(ies). All are already in AgentHub.", "Scan Complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        StatusText.Text = $"Scan finished. {discovered.Count} repos found, {addedCount} new added.";
    }

    private async void AddExisting_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "Select an existing Git repository",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false
        };
        if (dialog.ShowDialog() != Forms.DialogResult.OK) return;

        if (!await _git.IsRepositoryAsync(dialog.SelectedPath))
        {
            MessageBox.Show("That folder is not a Git repository.", "AgentHub", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_settings.Repositories.Any(r => string.Equals(r.LocalPath, dialog.SelectedPath, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show("That repository is already in AgentHub.", "AgentHub");
            return;
        }

        var repo = new RepositoryDefinition
        {
            Name = Path.GetFileName(dialog.SelectedPath.TrimEnd(Path.DirectorySeparatorChar)),
            LocalPath = dialog.SelectedPath,
            RemoteUrl = await _git.GetRemoteUrlAsync(dialog.SelectedPath)
        };
        _settings.Repositories.Add(repo);
        await _settingsService.SaveAsync(_settings);
        _selectedRepo = repo;
        RefreshRepoList();
        RepoList.SelectedItem = repo;
        StatusText.Text = $"Added {repo.Name}";
    }

    private async void CloneRepo_Click(object sender, RoutedEventArgs e)
    {
        var url = Microsoft.VisualBasic.Interaction.InputBox(
            "Paste the GitHub repository URL (HTTPS or SSH):",
            "Clone repository", "");
        if (string.IsNullOrWhiteSpace(url)) return;

        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "Choose the parent folder where the repository will be cloned",
            UseDescriptionForTitle = true
        };
        if (dialog.ShowDialog() != Forms.DialogResult.OK) return;

        var repoName = GetRepoNameFromUrl(url);
        var destination = Path.Combine(dialog.SelectedPath, repoName);
        if (Directory.Exists(destination))
        {
            MessageBox.Show($"Destination already exists:\n{destination}", "AgentHub", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        await RunGitActionAsync("Cloning", () => _git.CloneAsync(url.Trim(), destination));
        if (!Directory.Exists(destination) || !await _git.IsRepositoryAsync(destination)) return;

        var repo = new RepositoryDefinition { Name = repoName, LocalPath = destination, RemoteUrl = url.Trim() };
        _settings.Repositories.Add(repo);
        await _settingsService.SaveAsync(_settings);
        _selectedRepo = repo;
        RefreshRepoList();
        RepoList.SelectedItem = repo;
    }

    private async Task RefreshGitAsync()
    {
        if (_selectedRepo is null) return;
        if (!Directory.Exists(_selectedRepo.LocalPath))
        {
            StatusText.Text = "Repository folder no longer exists.";
            return;
        }

        try
        {
            var snapshot = await _git.GetSnapshotAsync(_selectedRepo.LocalPath);
            BranchText.Text = snapshot.Branch;
            ChangesText.Text = snapshot.ChangedFiles.ToString();
            AheadText.Text = snapshot.Ahead.ToString();
            BehindText.Text = snapshot.Behind.ToString();
            RemoteText.Text = snapshot.Remote;
            GitOutput.Text = string.IsNullOrWhiteSpace(snapshot.RawStatus) ? "Working tree clean." : snapshot.RawStatus;
            _selectedRepo.RemoteUrl = snapshot.Remote == "No origin" ? null : snapshot.Remote;
            await _settingsService.SaveAsync(_settings);
            StatusText.Text = snapshot.IsClean ? "Working tree clean" : $"{snapshot.ChangedFiles} changed file(s)";
        }
        catch (Exception ex)
        {
            GitOutput.Text = ex.ToString();
            StatusText.Text = "Could not read Git status.";
        }
    }

    private async void RefreshGit_Click(object sender, RoutedEventArgs e) => await RefreshGitAsync();
    private async void Fetch_Click(object sender, RoutedEventArgs e) => await RunGitActionAsync("Fetching", () => _git.FetchAsync(SelectedPath()));
    private async void Pull_Click(object sender, RoutedEventArgs e) => await RunGitActionAsync("Pulling", () => _git.PullAsync(SelectedPath()));
    private async void Push_Click(object sender, RoutedEventArgs e) => await RunGitActionAsync("Pushing", () => _git.PushAsync(SelectedPath()));

    private async Task RunGitActionAsync(string verb, Func<Task<(int ExitCode, string StdOut, string StdErr)>> action)
    {
        try
        {
            StatusText.Text = verb + "…";
            var result = await action();
            GitOutput.Text = (result.StdOut + Environment.NewLine + result.StdErr).Trim();
            StatusText.Text = result.ExitCode == 0 ? verb + " complete" : verb + $" failed (exit {result.ExitCode})";
            if (_selectedRepo is not null) await RefreshGitAsync();
        }
        catch (Exception ex)
        {
            GitOutput.Text = ex.ToString();
            StatusText.Text = verb + " failed";
        }
    }

    private void LaunchAgent_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedRepo is null || AgentCombo.SelectedItem is not AgentDefinition agent) return;
        try
        {
            _agentLauncher.Launch(agent, _selectedRepo.LocalPath, _settings.PreferWindowsTerminal);
            StatusText.Text = $"Launched {agent.Name} in {_selectedRepo.Name}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message + "\n\nCheck the command in AgentHub settings and ensure the CLI is on PATH.", "Could not launch agent", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenHandoff_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedRepo is not null) _repoContext.OpenHandoff(_selectedRepo.LocalPath);
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedRepo is not null) _repoContext.OpenFolder(_selectedRepo.LocalPath);
    }

    private void OpenGitHub_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedRepo?.RemoteUrl is { Length: > 0 } remote) _repoContext.OpenRemote(remote);
        else MessageBox.Show("This repository does not have an origin remote.", "AgentHub");
    }

    private async void RemoveRepo_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedRepo is null) return;
        var answer = MessageBox.Show($"Remove {_selectedRepo.Name} from AgentHub?\n\nThe local files and GitHub repository will not be deleted.", "Remove repository", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        _settings.Repositories.RemoveAll(r => r.Id == _selectedRepo.Id);
        _selectedRepo = null;
        await _settingsService.SaveAsync(_settings);
        RefreshRepoList();
        RepoDetail.Visibility = Visibility.Collapsed;
        StatusText.Text = "Repository removed from AgentHub";
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo("notepad.exe", $"\"{_settingsService.SettingsPath}\"") { UseShellExecute = true });
        StatusText.Text = "Edit settings.json, save it, then restart AgentHub to reload agent commands.";
    }

    private string SelectedPath() => _selectedRepo?.LocalPath ?? throw new InvalidOperationException("No repository selected.");

    private void UpdateAgentPreview()
    {
        if (AgentCombo.SelectedItem is AgentDefinition agent)
            AgentCommandPreview.Text = $"> {agent.Command} {agent.Arguments}".TrimEnd();
    }

    private static string GetRepoNameFromUrl(string url)
    {
        var value = url.Trim().TrimEnd('/');
        var slash = Math.Max(value.LastIndexOf('/'), value.LastIndexOf(':'));
        var name = slash >= 0 ? value[(slash + 1)..] : value;
        if (name.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return string.IsNullOrWhiteSpace(name) ? "repository" : name;
    }

    private static readonly SolidColorBrush NavActiveBrush = new(Color.FromRgb(59, 130, 246));
    private static readonly SolidColorBrush NavInactiveBrush = new(Color.FromRgb(42, 46, 54));

    // Opens the dedicated repo/GitHub management area, defaulting to the Local Repos sub-view.
    private void ManageReposBtn_Click(object sender, RoutedEventArgs e) => ViewReposBtn_Click(sender, e);

    private void ViewReposBtn_Click(object sender, RoutedEventArgs e)
    {
        RepoViewGrid.Visibility = Visibility.Visible;
        GitHubViewGrid.Visibility = Visibility.Collapsed;
        CockpitViewGrid.Visibility = Visibility.Collapsed;
        ManageBar.Visibility = Visibility.Visible;
        ManageReposBtn.Background = NavActiveBrush;
        ViewCockpitBtn.Background = NavInactiveBrush;
        ViewReposBtn.Background = NavActiveBrush;
        ViewGitHubBtn.Background = NavInactiveBrush;
    }

    private async void ViewGitHubBtn_Click(object sender, RoutedEventArgs e)
    {
        RepoViewGrid.Visibility = Visibility.Collapsed;
        GitHubViewGrid.Visibility = Visibility.Visible;
        CockpitViewGrid.Visibility = Visibility.Collapsed;
        ManageBar.Visibility = Visibility.Visible;
        ManageReposBtn.Background = NavActiveBrush;
        ViewCockpitBtn.Background = NavInactiveBrush;
        ViewGitHubBtn.Background = NavActiveBrush;
        ViewReposBtn.Background = NavInactiveBrush;

        if (_gitHubLoading)
        {
            return; // a load is already streaming in; the list updates as pages arrive
        }

        if (_allGitHubRepos.Count == 0)
        {
            await LoadGitHubReposAsync();
        }
        else
        {
            ApplyGitHubFilter();
        }
    }

    private void ViewCockpitBtn_Click(object sender, RoutedEventArgs e)
    {
        RepoViewGrid.Visibility = Visibility.Collapsed;
        GitHubViewGrid.Visibility = Visibility.Collapsed;
        CockpitViewGrid.Visibility = Visibility.Visible;
        ManageBar.Visibility = Visibility.Collapsed;
        ViewCockpitBtn.Background = NavActiveBrush;
        ManageReposBtn.Background = NavInactiveBrush;

        if (_lastUsageRefresh is null || DateTimeOffset.Now - _lastUsageRefresh > _usageRefreshTimer.Interval)
            _ = RefreshUsageAsync();
    }

    private async void RefreshUsage_Click(object sender, RoutedEventArgs e) => await RefreshUsageAsync();

    private async Task RefreshUsageAsync()
    {
        if (_usageRefreshInProgress) return;

        _usageRefreshInProgress = true;
        UsageRefreshButton.IsEnabled = false;
        UsageRefreshStatusText.Text = "Refreshing...";

        var activeProviders = _settings.UsageProviders.Where(p => p.Enabled).ToList();
        if (activeProviders.Count == 0)
        {
            UsageCards.ItemsSource = null;
            UsageCards.Visibility = Visibility.Collapsed;
            UsageEmptyNotice.Visibility = Visibility.Visible;
            UsageRefreshStatusText.Text = "No active providers configured";
            UsageRefreshButton.IsEnabled = true;
            _usageRefreshInProgress = false;
            return;
        }

        UsageCards.Visibility = Visibility.Visible;
        UsageEmptyNotice.Visibility = Visibility.Collapsed;

        if (UsageCards.ItemsSource is null)
        {
            UsageCards.ItemsSource = activeProviders
                .Select(provider => ToUsageDisplay(new UsageSnapshot(
                    provider.Id, provider.Name, DateTimeOffset.Now, [], provider.Command,
                    UsageCollectionStatus.Failed, "Refreshing usage...")))
                .ToList();
        }

        try
        {
            var snapshots = await _usageService.RefreshAsync(activeProviders);
            var displaySnapshots = snapshots.Select(snapshot =>
            {
                if (snapshot.Status == UsageCollectionStatus.Available)
                {
                    _lastSuccessfulUsage[snapshot.ProviderId] = snapshot;
                    return snapshot;
                }

                if (!_lastSuccessfulUsage.TryGetValue(snapshot.ProviderId, out var previous))
                    return snapshot;

                return previous with
                {
                    Status = UsageCollectionStatus.Stale,
                    Error = $"Last refresh failed: {snapshot.Error}"
                };
            }).ToList();

            UsageCards.ItemsSource = displaySnapshots.Select(ToUsageDisplay).ToList();
            _lastUsageRefresh = DateTimeOffset.Now;

            var available = snapshots.Count(snapshot => snapshot.Status == UsageCollectionStatus.Available);
            UsageRefreshStatusText.Text = $"Updated {_lastUsageRefresh:t} | {available}/{snapshots.Count} available";

            foreach (var s in snapshots)
            {
                _latestSnapshots[s.ProviderId] = s;
            }
            UpdateActiveFeedView();
        }
        catch (Exception ex)
        {
            UsageRefreshStatusText.Text = $"Refresh failed: {ex.Message}";
        }
        finally
        {
            UsageRefreshButton.IsEnabled = true;
            _usageRefreshInProgress = false;
        }
    }

    private void ToggleRawOutput_Click(object sender, RoutedEventArgs e)
    {
        RawOutputPanel.Visibility = RawOutputPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
        if (RawOutputPanel.Visibility == Visibility.Visible)
        {
            UpdateActiveFeedView();
        }
    }

    private void FeedTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string providerId)
        {
            _selectedFeedProviderId = providerId;
            UpdateActiveFeedView();
        }
    }

    private void CopyCurrentFeed_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(ActiveFeedTextBox?.Text))
        {
            Clipboard.SetText(ActiveFeedTextBox.Text);
            StatusText.Text = $"Copied {_selectedFeedProviderId} terminal feed to clipboard.";
        }
    }

    private void ToggleRawAnsi_Click(object sender, RoutedEventArgs e)
    {
        _showRawAnsi = !_showRawAnsi;
        if (RawAnsiToggleBtn != null)
        {
            RawAnsiToggleBtn.Content = _showRawAnsi ? "Raw ANSI" : "Sanitized";
        }
        UpdateActiveFeedView();
    }

    private void UpdateActiveFeedView()
    {
        if (ActiveFeedTextBox == null) return;

        var tabButtons = new[]
        {
            (FeedTabAntigravity, "gemini", Color.FromRgb(37, 99, 235)),
            (FeedTabCodex, "codex", Color.FromRgb(16, 185, 129)),
            (FeedTabClaude, "claude", Color.FromRgb(249, 115, 22))
        };

        foreach (var (btn, id, activeColor) in tabButtons)
        {
            if (btn == null) continue;
            var isSelected = string.Equals(_selectedFeedProviderId, id, StringComparison.OrdinalIgnoreCase);
            btn.Background = isSelected
                ? new SolidColorBrush(activeColor)
                : new SolidColorBrush(Color.FromRgb(39, 39, 42));
            btn.Foreground = isSelected
                ? new SolidColorBrush(Color.FromRgb(255, 255, 255))
                : new SolidColorBrush(Color.FromRgb(161, 161, 170));
        }

        if (_latestSnapshots.TryGetValue(_selectedFeedProviderId, out var snap))
        {
            FeedHeaderMeta.Text = $"Provider: {snap.ProviderName} | Source: {snap.Source} | Captured: {snap.CapturedAt:HH:mm:ss}";
            if (!string.IsNullOrWhiteSpace(snap.Error))
            {
                FeedHeaderMeta.Text += $" | Error: {snap.Error}";
            }

            var (badgeText, badgeBg, badgeFg) = snap.Status switch
            {
                UsageCollectionStatus.Available => ("AVAILABLE", Color.FromRgb(6, 95, 70), Color.FromRgb(52, 211, 153)),
                UsageCollectionStatus.Stale => ("STALE", Color.FromRgb(120, 53, 15), Color.FromRgb(251, 191, 36)),
                UsageCollectionStatus.TimedOut => ("TIMED OUT", Color.FromRgb(127, 29, 29), Color.FromRgb(248, 113, 113)),
                UsageCollectionStatus.UnsupportedOutput => ("OUTPUT CHANGED", Color.FromRgb(120, 53, 15), Color.FromRgb(251, 191, 36)),
                _ => ("FAILED", Color.FromRgb(127, 29, 29), Color.FromRgb(248, 113, 113))
            };

            FeedStatusBadge.Text = badgeText;
            FeedStatusBadge.Foreground = new SolidColorBrush(badgeFg);
            FeedStatusBadgeBorder.Background = new SolidColorBrush(badgeBg);

            if (!string.IsNullOrWhiteSpace(snap.RawOutput))
            {
                ActiveFeedTextBox.Text = _showRawAnsi
                    ? snap.RawOutput.Trim()
                    : UsageOutputParser.ExtractRelevantScreen(snap.RawOutput, _selectedFeedProviderId);
            }
            else
            {
                ActiveFeedTextBox.Text = "(No terminal output captured for this provider yet)";
            }
        }
        else
        {
            FeedHeaderMeta.Text = $"Provider: {_selectedFeedProviderId} | Waiting for capture...";
            FeedStatusBadge.Text = "WAITING";
            FeedStatusBadge.Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184));
            FeedStatusBadgeBorder.Background = new SolidColorBrush(Color.FromRgb(30, 41, 59));
            ActiveFeedTextBox.Text = "No capture data for this provider yet. Click 'Refresh usage' to run collection.";
        }
    }

    // Usage refresh interval is user-controlled (minutes) and persisted to settings.
    // 0.25 min (15s) floor protects the provider APIs; 120 min ceiling keeps it sane.
    private const double MinUsageIntervalMinutes = 0.25;
    private const double MaxUsageIntervalMinutes = 120;

    private void UpdateUsagePollingInterval()
    {
        var minutes = Math.Clamp(_settings.UsageRefreshMinutes, MinUsageIntervalMinutes, MaxUsageIntervalMinutes);
        _usageRefreshTimer.Interval = TimeSpan.FromMinutes(minutes);

        if (UsagePollingBadge != null)
        {
            var activeCount = _panes.Count(p => p.IsRunning);
            UsagePollingBadge.Text = activeCount > 0
                ? $"⚡ {activeCount} ACTIVE · EVERY {FormatInterval(minutes)}"
                : $"EVERY {FormatInterval(minutes)}";
        }
    }

    private void UsageIntervalBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) ApplyUsageInterval();
    }

    private void UsageIntervalBox_LostFocus(object sender, RoutedEventArgs e) => ApplyUsageInterval();

    private void ApplyUsageInterval()
    {
        if (UsageIntervalBox == null) return;

        var minutes = double.TryParse(UsageIntervalBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? Math.Clamp(parsed, MinUsageIntervalMinutes, MaxUsageIntervalMinutes)
            : _settings.UsageRefreshMinutes;

        _settings.UsageRefreshMinutes = minutes;
        UsageIntervalBox.Text = minutes.ToString("0.##", CultureInfo.InvariantCulture);

        UpdateUsagePollingInterval();
        _ = _settingsService.SaveAsync(_settings);
        _ = RefreshUsageAsync();
    }

    private static string FormatInterval(double minutes) =>
        minutes < 1 ? $"{minutes * 60:0}S" : $"{minutes:0.##}M";

    // Human-friendly "when does this limit reset" text. Near-term windows (e.g. the 5-hour
    // limit) show a countdown; further-out ones show the day and clock time.
    private static string FormatReset(UsageLimit limit)
    {
        if (limit.ResetsAt is { } resetsAt)
        {
            var remaining = resetsAt - DateTimeOffset.Now;
            if (remaining <= TimeSpan.Zero) return "resets now";
            if (remaining < TimeSpan.FromHours(24))
            {
                var hours = (int)remaining.TotalHours;
                var mins = remaining.Minutes;
                var span = hours > 0 ? $"{hours}h {mins}m" : $"{mins}m";
                return $"resets in {span}";
            }
            return $"resets {resetsAt.ToLocalTime():ddd h:mm tt}";
        }

        return string.IsNullOrWhiteSpace(limit.ResetText) ? "" : $"resets {limit.ResetText}";
    }

    private static UsageProviderDisplay ToUsageDisplay(UsageSnapshot snapshot)
    {
        var accent = snapshot.ProviderId.ToLowerInvariant() switch
        {
            "codex" => new SolidColorBrush(Color.FromRgb(52, 211, 153)),
            "claude" => new SolidColorBrush(Color.FromRgb(251, 146, 60)),
            "gemini" => new SolidColorBrush(Color.FromRgb(96, 165, 250)),
            _ => new SolidColorBrush(Color.FromRgb(156, 163, 175))
        };

        var limits = snapshot.Limits.Select(limit =>
        {
            // Show percentage LEFT (bar full = plenty remaining, red as it runs low).
            var remaining = Math.Clamp(limit.RemainingFraction * 100d, 0d, 100d);
            var barBrush = remaining switch
            {
                <= 15 => new SolidColorBrush(Color.FromRgb(239, 68, 68)),
                <= 35 => new SolidColorBrush(Color.FromRgb(245, 158, 11)),
                _ => new SolidColorBrush(Color.FromRgb(34, 197, 94))
            };
            var reset = FormatReset(limit);
            return new UsageLimitDisplay
            {
                Name = limit.Name,
                RemainingPercent = remaining,
                ValueText = $"{remaining:0}% left",
                ResetText = reset,
                BarBrush = barBrush
            };
        }).ToList();

        var status = snapshot.Status == UsageCollectionStatus.Available
            ? "Current"
            : snapshot.Status switch
            {
                UsageCollectionStatus.Stale => "Stale",
                UsageCollectionStatus.NotInstalled => "Not installed",
                UsageCollectionStatus.TimedOut => "Timed out",
                UsageCollectionStatus.UnsupportedOutput => "Output changed",
                UsageCollectionStatus.Failed => "Failed",
                _ => "Unavailable"
            };
        var detail = snapshot.Status is UsageCollectionStatus.Available
            ? $"Source: {snapshot.Source} | updated {snapshot.CapturedAt:t}"
            : snapshot.Status == UsageCollectionStatus.Stale
                ? $"Updated {snapshot.CapturedAt:t} | {Truncate(snapshot.Error ?? "Refresh failed.", 90)}"
            : Truncate(snapshot.Error ?? "Usage refresh failed.", 140);

        return new UsageProviderDisplay
        {
            Name = snapshot.ProviderName,
            StatusText = status,
            SourceText = detail,
            AccentBrush = accent,
            Limits = limits
        };
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..(maxLength - 3)] + "...";

    private async Task LoadGitHubReposAsync(bool forceRefresh = false)
    {
        if (_allGitHubRepos.Count > 0 && !forceRefresh)
        {
            UpdateGitHubAddedState();
            ApplyGitHubFilter();
            return;
        }

        // Cancel any load still streaming from a previous account/refresh so its repos don't bleed into this list.
        _gitHubLoadCts?.Cancel();
        var cts = new CancellationTokenSource();
        _gitHubLoadCts = cts;
        _gitHubLoading = true;

        GitHubLoadingText.Text = "Checking GitHub CLI account…";
        GitHubLoadingText.Visibility = Visibility.Visible;
        GitHubAuthWarning.Visibility = Visibility.Collapsed;
        GitHubRepoList.Visibility = Visibility.Collapsed;
        _allGitHubRepos = new List<GitHubRepository>();
        GitHubRepoList.ItemsSource = null;

        await RefreshGitHubAccountsAsync();
        if (cts.IsCancellationRequested) return;

        var (isAuth, username) = await _githubService.GetAuthUserAsync();
        if (!isAuth || string.IsNullOrWhiteSpace(username))
        {
            GitHubLoadingText.Visibility = Visibility.Collapsed;
            GitHubAuthWarning.Visibility = Visibility.Visible;
            GitHubAccountText.Text = _gitHubAccounts.Count == 0 ? "Not signed in" : "Active account unavailable";
            GitHubAccountText.Visibility = Visibility.Visible;
            GitHubRepoCountText.Text = _gitHubAccounts.Count == 0
                ? "Click Sign in to connect a GitHub account."
                : "GitHub CLI could not use the active account. Pick another account or sign in again.";
            _gitHubLoading = false;
            return;
        }

        if (cts.IsCancellationRequested) return;

        GitHubAccountText.Text = $"@{username}";
        GitHubAccountText.Visibility = _gitHubAccounts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        GitHubAuthWarning.Visibility = Visibility.Collapsed;
        GitHubLoadingText.Text = $"Fetching repositories visible to @{username} (own, shared and organisation)…";

        // Repos arrive page by page on a worker thread; batch them onto the UI thread and re-render every so often
        // so a large organisation shows results within a second or two instead of after the whole fetch.
        var pending = new List<GitHubRepository>();
        var lastRender = DateTime.UtcNow;
        var progress = new Progress<GitHubRepository>(repo =>
        {
            if (cts.IsCancellationRequested) return;
            pending.Add(repo);

            if (pending.Count >= 25 || (DateTime.UtcNow - lastRender).TotalMilliseconds > 400)
            {
                FlushPendingGitHubRepos(pending, username, complete: false);
                lastRender = DateTime.UtcNow;
            }
        });

        try
        {
            var (count, error) = await _githubService.StreamAllRepositoriesAsync(
                username,
                repo => ((IProgress<GitHubRepository>)progress).Report(repo),
                cts.Token);

            if (cts.IsCancellationRequested) return;

            // Let any Progress callbacks already posted to the dispatcher land before the final flush.
            await System.Windows.Threading.Dispatcher.Yield(DispatcherPriority.Background);
            FlushPendingGitHubRepos(pending, username, complete: true);

            if (error is not null)
            {
                GitHubLoadingText.Text = "Could not load repositories: " + error;
                GitHubLoadingText.Visibility = Visibility.Visible;
                StatusText.Text = "Failed to load GitHub repositories: " + error;
                return;
            }

            var pushable = _allGitHubRepos.Count(r => r.CanPush);
            StatusText.Text = $"Loaded {_allGitHubRepos.Count} GitHub repositories for @{username} · push access on {pushable}, clone-only on {_allGitHubRepos.Count - pushable}";
        }
        catch (Exception ex)
        {
            GitHubLoadingText.Visibility = Visibility.Collapsed;
            StatusText.Text = "Failed to load GitHub repositories: " + ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_gitHubLoadCts, cts)) _gitHubLoading = false;
        }
    }

    private void FlushPendingGitHubRepos(List<GitHubRepository> pending, string username, bool complete)
    {
        if (pending.Count > 0)
        {
            _allGitHubRepos.AddRange(pending);
            pending.Clear();
        }

        UpdateGitHubAddedState();

        if (_allGitHubRepos.Count > 0)
        {
            GitHubLoadingText.Visibility = Visibility.Collapsed;
            GitHubRepoList.Visibility = Visibility.Visible;
        }
        else if (complete)
        {
            GitHubLoadingText.Text = $"@{username} has no repositories visible through GitHub CLI.";
            GitHubLoadingText.Visibility = Visibility.Visible;
        }

        ApplyGitHubFilter();

        if (!complete)
        {
            StatusText.Text = $"Loading GitHub repositories for @{username}… {_allGitHubRepos.Count} so far";
        }
    }

    public async Task RefreshAllRepoSnapshotsAsync(bool fetchRemotes = false)
    {
        if (_settings.Repositories.Count == 0) return;

        StatusText.Text = fetchRemotes ? "Fetching remote status for all repositories…" : "Checking status of all local repositories…";

        foreach (var repo in _settings.Repositories)
        {
            if (!Directory.Exists(repo.LocalPath))
                continue;

            try
            {
                if (fetchRemotes)
                {
                    await _git.FetchAsync(repo.LocalPath);
                }

                var snapshot = await _git.GetSnapshotAsync(repo.LocalPath);
                repo.CurrentBranch = snapshot.Branch;
                repo.Ahead = snapshot.Ahead;
                repo.Behind = snapshot.Behind;
                repo.ChangedFiles = snapshot.ChangedFiles;
            }
            catch { }
        }

        RepoList.ItemsSource = null;
        RepoList.ItemsSource = _settings.Repositories.OrderBy(r => r.Name).ToList();

        UpdateGitHubAddedState();
        ApplyGitHubFilter();

        StatusText.Text = fetchRemotes ? "All repositories fetched and sync states updated." : "Repository sync states updated.";
    }

    private void UpdateGitHubAddedState()
    {
        foreach (var ghRepo in _allGitHubRepos)
        {
            var match = _settings.Repositories.FirstOrDefault(r =>
                RepoDiscoveryService.MatchesUrl(r.RemoteUrl, ghRepo.Url) ||
                string.Equals(r.Name, ghRepo.Name, StringComparison.OrdinalIgnoreCase));

            if (match != null)
            {
                ghRepo.IsAlreadyAdded = true;
                ghRepo.LocalPath = match.LocalPath;
                ghRepo.LocalExists = match.ExistsOnDisk;
                ghRepo.LocalBranch = match.CurrentBranch;
                ghRepo.AheadCount = match.Ahead;
                ghRepo.BehindCount = match.Behind;
                ghRepo.ChangedFilesCount = match.ChangedFiles;
            }
            else
            {
                ghRepo.IsAlreadyAdded = false;
                ghRepo.LocalPath = null;
                ghRepo.LocalExists = false;
                ghRepo.LocalBranch = null;
                ghRepo.AheadCount = 0;
                ghRepo.BehindCount = 0;
                ghRepo.ChangedFilesCount = 0;
            }
        }
    }

    private void ApplyGitHubFilter()
    {
        if (_allGitHubRepos == null || GitHubRepoList == null) return;

        var query = GitHubSearchBox?.Text?.Trim() ?? string.Empty;
        var filterTag = (GitHubVisibilityFilter?.SelectedItem as ComboBoxItem)?.Tag as string ?? "all";

        var filtered = _allGitHubRepos.Where(r =>
        {
            if (!string.IsNullOrWhiteSpace(query))
            {
                var matchesName = r.NameWithOwner.Contains(query, StringComparison.OrdinalIgnoreCase);
                var matchesDesc = (r.Description ?? string.Empty).Contains(query, StringComparison.OrdinalIgnoreCase);
                var matchesOwner = r.OwnerLogin.Contains(query, StringComparison.OrdinalIgnoreCase);
                if (!matchesName && !matchesDesc && !matchesOwner) return false;
            }

            return filterTag switch
            {
                "push" => r.CanPush,
                "readonly" => !r.CanPush,
                "mine" => r.IsOwnedByViewer,
                "org" => r.IsOwnedByOrganization,
                "archived" => r.IsArchived,
                "uptodate" => r.IsAlreadyAdded && r.LocalExists && r.BehindCount == 0 && r.AheadCount == 0 && r.ChangedFilesCount == 0,
                "behind" => r.IsAlreadyAdded && r.LocalExists && r.BehindCount > 0,
                "ahead" => r.IsAlreadyAdded && r.LocalExists && r.AheadCount > 0,
                "dirty" => r.IsAlreadyAdded && r.LocalExists && r.ChangedFilesCount > 0,
                "notadded" => !r.IsAlreadyAdded,
                "added" => r.IsAlreadyAdded,
                "private" => r.IsPrivate,
                "public" => !r.IsPrivate,
                _ => true
            };
        }).ToList();

        // Group by access category (mine → pushable → read-only → archived), newest push first inside each group.
        var view = new System.Windows.Data.ListCollectionView(filtered);
        view.SortDescriptions.Add(new System.ComponentModel.SortDescription(nameof(GitHubRepository.CategoryOrder), System.ComponentModel.ListSortDirection.Ascending));
        view.SortDescriptions.Add(new System.ComponentModel.SortDescription(nameof(GitHubRepository.Category), System.ComponentModel.ListSortDirection.Ascending));
        view.SortDescriptions.Add(new System.ComponentModel.SortDescription(nameof(GitHubRepository.PushedAt), System.ComponentModel.ListSortDirection.Descending));
        view.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription(nameof(GitHubRepository.Category)));

        GitHubRepoList.ItemsSource = view;

        if (GitHubRepoCountText != null)
        {
            var pushable = filtered.Count(r => r.CanPush);
            GitHubRepoCountText.Text = filtered.Count == _allGitHubRepos.Count
                ? $"{filtered.Count} repositories · push access on {pushable} · clone-only on {filtered.Count - pushable}"
                : $"{filtered.Count} of {_allGitHubRepos.Count} repositories shown · push access on {pushable} · clone-only on {filtered.Count - pushable}";
        }
    }

    private async void RefreshGitHub_Click(object sender, RoutedEventArgs e) => await LoadGitHubReposAsync(forceRefresh: true);

    private async void AutoDetectRepos_Click(object sender, RoutedEventArgs e)
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        StatusText.Text = "Scanning Desktop for local repositories…";
        var discovered = await _discovery.ScanFolderAsync(desktop, maxDepth: 2);
        int added = 0;
        foreach (var d in discovered)
        {
            if (!_settings.Repositories.Any(r => string.Equals(r.LocalPath, d.LocalPath, StringComparison.OrdinalIgnoreCase)))
            {
                _settings.Repositories.Add(new RepositoryDefinition
                {
                    Name = d.Name,
                    LocalPath = d.LocalPath,
                    RemoteUrl = d.RemoteUrl,
                    CurrentBranch = d.Snapshot?.Branch,
                    Ahead = d.Snapshot?.Ahead ?? 0,
                    Behind = d.Snapshot?.Behind ?? 0,
                    ChangedFiles = d.Snapshot?.ChangedFiles ?? 0
                });
                added++;
            }
        }

        if (added > 0)
        {
            await _settingsService.SaveAsync(_settings);
            RefreshRepoList();
        }

        await RefreshAllRepoSnapshotsAsync(fetchRemotes: false);
        StatusText.Text = $"Auto-detected {discovered.Count} local repos ({added} new added).";
        MessageBox.Show($"Auto-detect complete!\n\nFound {discovered.Count} local repositories on Desktop.\nLinked with remote GitHub repositories.", "Local Repos Detected", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void CheckAllSync_Click(object sender, RoutedEventArgs e)
    {
        await RefreshAllRepoSnapshotsAsync(fetchRemotes: true);
    }

    private async void GitHubPull_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement el || el.Tag is not GitHubRepository ghRepo || string.IsNullOrWhiteSpace(ghRepo.LocalPath))
            return;

        StatusText.Text = $"Pulling fast-forward for {ghRepo.Name}…";
        var res = await _git.PullAsync(ghRepo.LocalPath);
        if (res.ExitCode == 0)
        {
            var snap = await _git.GetSnapshotAsync(ghRepo.LocalPath);
            var local = _settings.Repositories.FirstOrDefault(r => string.Equals(r.LocalPath, ghRepo.LocalPath, StringComparison.OrdinalIgnoreCase));
            if (local != null)
            {
                local.Ahead = snap.Ahead;
                local.Behind = snap.Behind;
                local.ChangedFiles = snap.ChangedFiles;
                local.CurrentBranch = snap.Branch;
            }
            UpdateGitHubAddedState();
            ApplyGitHubFilter();
            StatusText.Text = $"Successfully pulled {ghRepo.Name}. Up to date!";
        }
        else
        {
            MessageBox.Show($"Pull failed:\n{res.StdErr}\n{res.StdOut}", "Pull Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            StatusText.Text = $"Pull failed for {ghRepo.Name}.";
        }
    }

    private async void GitHubPush_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement el || el.Tag is not GitHubRepository ghRepo || string.IsNullOrWhiteSpace(ghRepo.LocalPath))
            return;

        if (!ghRepo.CanPush)
        {
            MessageBox.Show(this, ghRepo.AccessTooltip, "Push not permitted", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        StatusText.Text = $"Pushing commits for {ghRepo.Name} to GitHub…";
        var res = await _git.PushAsync(ghRepo.LocalPath);
        if (res.ExitCode == 0)
        {
            var snap = await _git.GetSnapshotAsync(ghRepo.LocalPath);
            var local = _settings.Repositories.FirstOrDefault(r => string.Equals(r.LocalPath, ghRepo.LocalPath, StringComparison.OrdinalIgnoreCase));
            if (local != null)
            {
                local.Ahead = snap.Ahead;
                local.Behind = snap.Behind;
                local.ChangedFiles = snap.ChangedFiles;
                local.CurrentBranch = snap.Branch;
            }
            UpdateGitHubAddedState();
            ApplyGitHubFilter();
            StatusText.Text = $"Successfully pushed {ghRepo.Name} to GitHub!";
        }
        else
        {
            MessageBox.Show($"Push failed:\n{res.StdErr}\n{res.StdOut}", "Push Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            StatusText.Text = $"Push failed for {ghRepo.Name}.";
        }
    }

    private void GitHubCockpit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement el || el.Tag is not GitHubRepository ghRepo || string.IsNullOrWhiteSpace(ghRepo.LocalPath))
            return;

        ViewCockpitBtn_Click(sender, e);
        var target = _panes.FirstOrDefault() ?? CreateTerminalPane();
        if (!_panes.Contains(target)) { _panes.Add(target); RebuildTerminalLayout(); }

        var defaultShell = _shellOptions.FirstOrDefault(s => s.DisplayName.Contains("PowerShell")) ?? _shellOptions.FirstOrDefault();
        var cmd = defaultShell?.Command ?? "powershell.exe -NoLogo";
        target.StartSession(cmd, ghRepo.LocalPath, $"Terminal {target.PaneIndex} · {ghRepo.Name}", ghRepo.Name);
        StatusText.Text = $"Opened Cockpit in {ghRepo.Name} ({ghRepo.LocalPath})";
    }

    private void GitHubSearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyGitHubFilter();

    private void GitHubVisibilityFilter_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyGitHubFilter();

    private void GitHubRepoTitle_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement el && el.Tag is string url && !string.IsNullOrWhiteSpace(url))
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
    }

    private void OpenGitHubUrl_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement el && el.Tag is string url && !string.IsNullOrWhiteSpace(url))
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
    }

    /// <summary>Reloads the gh account list into the header combo without touching the repo list.</summary>
    private async Task RefreshGitHubAccountsAsync()
    {
        _gitHubAccounts = await _githubService.GetAccountsAsync();

        _suppressAccountSelection = true;
        try
        {
            GitHubAccountCombo.ItemsSource = null;
            GitHubAccountCombo.ItemsSource = _gitHubAccounts;
            GitHubAccountCombo.SelectedItem = _gitHubAccounts.FirstOrDefault(a => a.IsActive) ?? _gitHubAccounts.FirstOrDefault();

            var hasAccounts = _gitHubAccounts.Count > 0;
            GitHubAccountCombo.Visibility = hasAccounts ? Visibility.Visible : Visibility.Collapsed;
            GitHubSignOutButton.Visibility = hasAccounts ? Visibility.Visible : Visibility.Collapsed;
            GitHubAccountText.Visibility = hasAccounts ? Visibility.Collapsed : Visibility.Visible;
        }
        finally
        {
            _suppressAccountSelection = false;
        }
    }

    private async void GitHubAccountCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressAccountSelection) return;
        if (GitHubAccountCombo.SelectedItem is not GitHubAccount account || account.IsActive) return;

        StatusText.Text = $"Switching GitHub CLI to @{account.Login}…";
        GitHubAccountCombo.IsEnabled = false;
        try
        {
            var (ok, error) = await _githubService.SwitchAccountAsync(account.Host, account.Login);
            if (!ok)
            {
                StatusText.Text = $"Could not switch to @{account.Login}: {error}";
                await RefreshGitHubAccountsAsync();
                return;
            }

            StatusText.Text = $"GitHub CLI is now using @{account.Login}. Reloading repositories…";
            await LoadGitHubReposAsync(forceRefresh: true);
        }
        finally
        {
            GitHubAccountCombo.IsEnabled = true;
        }
    }

    private async void SignInGitHub_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new GitHubLoginWindow(_githubService) { Owner = this };
        var completed = dialog.ShowDialog() == true;

        if (!completed)
        {
            // Even a cancelled attempt can leave gh state unchanged; just make sure the list is current.
            await RefreshGitHubAccountsAsync();
            return;
        }

        StatusText.Text = string.IsNullOrWhiteSpace(dialog.SignedInLogin)
            ? "GitHub sign-in complete. Reloading repositories…"
            : $"Signed in as @{dialog.SignedInLogin}. Reloading repositories…";
        await LoadGitHubReposAsync(forceRefresh: true);
    }

    private async void SignOutGitHub_Click(object sender, RoutedEventArgs e)
    {
        if (GitHubAccountCombo.SelectedItem is not GitHubAccount account) return;

        var confirm = MessageBox.Show(
            this,
            $"Remove @{account.Login} ({account.Host}) from GitHub CLI on this machine?\n\nThis runs `gh auth logout`. You can sign back in at any time.",
            "Sign out of GitHub",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        var (ok, error) = await _githubService.LogoutAsync(account.Host, account.Login);
        StatusText.Text = ok
            ? $"Signed out @{account.Login} from GitHub CLI."
            : $"Could not sign out @{account.Login}: {error}";

        _allGitHubRepos.Clear();
        await LoadGitHubReposAsync(forceRefresh: true);
    }

    private void LoginGitHubInCockpit_Click(object sender, RoutedEventArgs e)
    {
        ViewCockpitBtn_Click(sender, e);
        var target = _panes.FirstOrDefault() ?? CreateTerminalPane();
        if (!_panes.Contains(target)) { _panes.Add(target); RebuildTerminalLayout(); }

        target.StartSession("gh auth login", Environment.CurrentDirectory, $"Terminal {target.PaneIndex} · GitHub CLI Login", "GitHub Auth");
        StatusText.Text = $"Follow prompts in Terminal {target.PaneIndex} to complete GitHub authentication.";
    }

    private async void GitHubAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement el || el.Tag is not GitHubRepository repo) return;

        if (repo.IsAlreadyAdded)
        {
            var local = _settings.Repositories.FirstOrDefault(r =>
                string.Equals(r.LocalPath, repo.LocalPath, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(r.Name, repo.Name, StringComparison.OrdinalIgnoreCase));

            if (local != null)
            {
                ViewReposBtn_Click(sender, e);
                RepoList.SelectedItem = local;
                StatusText.Text = $"Selected {local.Name} in Local Repos";
            }
            return;
        }

        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = $"Choose the parent folder where '{repo.Name}' will be cloned",
            UseDescriptionForTitle = true,
            SelectedPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
        };

        if (dialog.ShowDialog() != Forms.DialogResult.OK) return;

        var destination = Path.Combine(dialog.SelectedPath, repo.Name);
        if (Directory.Exists(destination))
        {
            MessageBox.Show($"Destination folder already exists:\n{destination}", "AgentHub", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        StatusText.Text = $"Cloning {repo.NameWithOwner}…";
        var cloneRes = await _git.CloneAsync(repo.Url, destination);
        if (cloneRes.ExitCode != 0)
        {
            MessageBox.Show($"Git clone failed:\n{cloneRes.StdErr}\n{cloneRes.StdOut}", "Clone failed", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusText.Text = "Clone failed.";
            return;
        }

        var newRepo = new RepositoryDefinition
        {
            Name = repo.Name,
            LocalPath = destination,
            RemoteUrl = repo.Url
        };

        _settings.Repositories.Add(newRepo);
        await _settingsService.SaveAsync(_settings);

        repo.IsAlreadyAdded = true;
        repo.LocalPath = destination;
        repo.LocalExists = true;

        RefreshRepoList();
        _ = RefreshAllRepoSnapshotsAsync();

        StatusText.Text = $"Cloned and registered {repo.Name} in AgentHub!";

        var answer = MessageBox.Show($"Successfully cloned {repo.Name}!\n\nWould you like to open it in Local Repositories?", "Repository Cloned", MessageBoxButton.YesNo, MessageBoxImage.Information);
        if (answer == MessageBoxResult.Yes)
        {
            ViewReposBtn_Click(sender, e);
            RepoList.SelectedItem = newRepo;
        }
    }

    private void LaunchCockpit1_Click(object sender, RoutedEventArgs e) => LaunchCockpitAtPane(0);
    private void LaunchCockpit2_Click(object sender, RoutedEventArgs e) => LaunchCockpitAtPane(1);
    private void LaunchCockpit3_Click(object sender, RoutedEventArgs e) => LaunchCockpitAtPane(2);
    private void LaunchCockpitLeft_Click(object sender, RoutedEventArgs e) => LaunchCockpitAtPane(0);
    private void LaunchCockpitRight_Click(object sender, RoutedEventArgs e) => LaunchCockpitAtPane(1);

    private void LaunchCockpitAtPane(int paneIndex)
    {
        if (_selectedRepo is null || AgentCombo.SelectedItem is not AgentDefinition agent) return;
        ViewCockpitBtn_Click(this, new RoutedEventArgs());

        bool added = false;
        while (_panes.Count <= paneIndex && _panes.Count < 6)
        {
            _panes.Add(CreateTerminalPane(_panes.Count, 0));
            added = true;
        }
        if (added)
        {
            RebuildTerminalLayout();
        }

        var target = _panes[Math.Min(paneIndex, _panes.Count - 1)];
        var cmd = string.IsNullOrWhiteSpace(agent.Arguments) ? agent.Command : $"{agent.Command} {agent.Arguments}";
        target.StartSession(cmd, _selectedRepo.LocalPath, $"Terminal {target.PaneIndex} · {agent.Name}", _selectedRepo.Name);
        StatusText.Text = $"Launched {agent.Name} in Terminal {target.PaneIndex}";
    }

    protected override void OnClosed(EventArgs e)
    {
        _usageRefreshTimer.Stop();
        foreach (var pane in _panes)
        {
            pane.Dispose();
        }
        base.OnClosed(e);
    }
}
