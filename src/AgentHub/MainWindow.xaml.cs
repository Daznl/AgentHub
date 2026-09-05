using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using AgentHub.Models;
using AgentHub.Services;
using AgentHub.Terminal;
using Color = System.Windows.Media.Color;
using Forms = System.Windows.Forms;
using MessageBox = System.Windows.MessageBox;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;

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
    private readonly List<ShellOption> _shellOptions = new();
    private List<GitHubRepository> _allGitHubRepos = new();
    private AppSettings _settings = new();
    private RepositoryDefinition? _selectedRepo;

    public MainWindow()
    {
        InitializeComponent();
        _git = new GitService(_runner);
        _githubService = new GitHubService(_runner);
        _discovery = new RepoDiscoveryService(_git);
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

        RefreshRepoList();
        RefreshAgentList();
        PopulateShellOptions();
        StatusText.Text = $"Settings: {_settingsService.SettingsPath}";

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

        LeftRepoCombo.ItemsSource = null;
        LeftRepoCombo.ItemsSource = repos;
        if (repos.Count > 0) LeftRepoCombo.SelectedIndex = 0;

        RightRepoCombo.ItemsSource = null;
        RightRepoCombo.ItemsSource = repos;
        if (repos.Count > 1) RightRepoCombo.SelectedIndex = 1;
        else if (repos.Count > 0) RightRepoCombo.SelectedIndex = 0;

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

        LeftShellCombo.ItemsSource = null;
        LeftShellCombo.ItemsSource = _shellOptions;
        if (_shellOptions.Count > 0) LeftShellCombo.SelectedIndex = 0;

        RightShellCombo.ItemsSource = null;
        RightShellCombo.ItemsSource = _shellOptions;
        if (_shellOptions.Count > 1) RightShellCombo.SelectedIndex = 1;
        else if (_shellOptions.Count > 0) RightShellCombo.SelectedIndex = 0;
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

    private void ViewReposBtn_Click(object sender, RoutedEventArgs e)
    {
        RepoViewGrid.Visibility = Visibility.Visible;
        GitHubViewGrid.Visibility = Visibility.Collapsed;
        CockpitViewGrid.Visibility = Visibility.Collapsed;
        ViewReposBtn.Background = NavActiveBrush;
        ViewGitHubBtn.Background = NavInactiveBrush;
        ViewCockpitBtn.Background = NavInactiveBrush;
    }

    private async void ViewGitHubBtn_Click(object sender, RoutedEventArgs e)
    {
        RepoViewGrid.Visibility = Visibility.Collapsed;
        GitHubViewGrid.Visibility = Visibility.Visible;
        CockpitViewGrid.Visibility = Visibility.Collapsed;
        ViewGitHubBtn.Background = NavActiveBrush;
        ViewReposBtn.Background = NavInactiveBrush;
        ViewCockpitBtn.Background = NavInactiveBrush;

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
        ViewCockpitBtn.Background = NavActiveBrush;
        ViewReposBtn.Background = NavInactiveBrush;
        ViewGitHubBtn.Background = NavInactiveBrush;
    }

    private async Task LoadGitHubReposAsync(bool forceRefresh = false)
    {
        if (_allGitHubRepos.Count > 0 && !forceRefresh)
        {
            UpdateGitHubAddedState();
            ApplyGitHubFilter();
            return;
        }

        GitHubLoadingText.Visibility = Visibility.Visible;
        GitHubAuthWarning.Visibility = Visibility.Collapsed;
        GitHubScrollViewer.Visibility = Visibility.Collapsed;

        var (isAuth, username) = await _githubService.GetAuthUserAsync();
        if (!isAuth || string.IsNullOrWhiteSpace(username))
        {
            GitHubLoadingText.Visibility = Visibility.Collapsed;
            GitHubAuthWarning.Visibility = Visibility.Visible;
            GitHubAccountText.Text = "Not authenticated";
            GitHubRepoCountText.Text = "Run gh auth login in terminal to connect your GitHub account.";
            return;
        }

        GitHubAccountText.Text = $"@{username}";
        GitHubAuthWarning.Visibility = Visibility.Collapsed;

        try
        {
            var repos = await _githubService.GetRepositoriesAsync(100);
            _allGitHubRepos = repos;

            UpdateGitHubAddedState();

            GitHubLoadingText.Visibility = Visibility.Collapsed;
            GitHubScrollViewer.Visibility = Visibility.Visible;
            ApplyGitHubFilter();
            StatusText.Text = $"Loaded {_allGitHubRepos.Count} GitHub repositories for @{username}";
        }
        catch (Exception ex)
        {
            GitHubLoadingText.Visibility = Visibility.Collapsed;
            StatusText.Text = "Failed to load GitHub repositories: " + ex.Message;
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
        var filterIndex = GitHubVisibilityFilter?.SelectedIndex ?? 0;

        var filtered = _allGitHubRepos.Where(r =>
        {
            if (!string.IsNullOrWhiteSpace(query))
            {
                var matchesName = r.NameWithOwner.Contains(query, StringComparison.OrdinalIgnoreCase);
                var matchesDesc = (r.Description ?? string.Empty).Contains(query, StringComparison.OrdinalIgnoreCase);
                if (!matchesName && !matchesDesc) return false;
            }

            return filterIndex switch
            {
                1 => r.IsAlreadyAdded && r.LocalExists && r.BehindCount == 0 && r.AheadCount == 0 && r.ChangedFilesCount == 0,
                2 => r.IsAlreadyAdded && r.LocalExists && r.BehindCount > 0,
                3 => r.IsAlreadyAdded && r.LocalExists && r.AheadCount > 0,
                4 => r.IsAlreadyAdded && r.LocalExists && r.ChangedFilesCount > 0,
                5 => !r.IsAlreadyAdded,
                6 => r.IsAlreadyAdded,
                7 => r.IsPrivate,
                8 => !r.IsPrivate,
                _ => true
            };
        }).ToList();

        GitHubRepoList.ItemsSource = null;
        GitHubRepoList.ItemsSource = filtered;

        if (GitHubRepoCountText != null)
            GitHubRepoCountText.Text = $"{filtered.Count} repository(ies) shown ({_allGitHubRepos.Count} total remote)";
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

    private void GitHubCockpit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement el || el.Tag is not GitHubRepository ghRepo || string.IsNullOrWhiteSpace(ghRepo.LocalPath))
            return;

        ViewCockpitBtn_Click(sender, e);
        var defaultShell = _shellOptions.FirstOrDefault(s => s.DisplayName.Contains("PowerShell")) ?? _shellOptions.FirstOrDefault();
        var cmd = defaultShell?.Command ?? "powershell.exe -NoLogo";
        TerminalLeft.StartSession(cmd, ghRepo.LocalPath, $"Terminal 1 · {ghRepo.Name}", ghRepo.Name);
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

    private void LoginGitHubInCockpit_Click(object sender, RoutedEventArgs e)
    {
        ViewCockpitBtn_Click(sender, e);
        TerminalLeft.StartSession("gh auth login", Environment.CurrentDirectory, "Terminal 1 · GitHub CLI Login", "GitHub Auth");
        StatusText.Text = "Follow prompts in Terminal 1 to complete GitHub authentication.";
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

    private void LaunchCockpitLeft_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedRepo is null || AgentCombo.SelectedItem is not AgentDefinition agent) return;
        ViewCockpitBtn_Click(sender, e);
        var cmd = string.IsNullOrWhiteSpace(agent.Arguments) ? agent.Command : $"{agent.Command} {agent.Arguments}";
        TerminalLeft.StartSession(cmd, _selectedRepo.LocalPath, $"Terminal 1 · {agent.Name}", _selectedRepo.Name);
        StatusText.Text = $"Launched {agent.Name} in Terminal 1 (Left)";
    }

    private void LaunchCockpitRight_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedRepo is null || AgentCombo.SelectedItem is not AgentDefinition agent) return;
        ViewCockpitBtn_Click(sender, e);
        var cmd = string.IsNullOrWhiteSpace(agent.Arguments) ? agent.Command : $"{agent.Command} {agent.Arguments}";
        TerminalRight.StartSession(cmd, _selectedRepo.LocalPath, $"Terminal 2 · {agent.Name}", _selectedRepo.Name);
        StatusText.Text = $"Launched {agent.Name} in Terminal 2 (Right)";
    }

    private void LaunchLeft_Click(object sender, RoutedEventArgs e)
    {
        var repo = LeftRepoCombo.SelectedItem as RepositoryDefinition;
        var dir = repo?.LocalPath ?? Directory.GetCurrentDirectory();
        var repoName = repo?.Name ?? "Workspace";
        var shell = LeftShellCombo.SelectedItem as ShellOption;
        var cmd = shell?.Command ?? "powershell.exe -NoLogo";
        var title = shell?.DisplayName ?? "Shell";
        TerminalLeft.StartSession(cmd, dir, $"Terminal 1 · {title}", repoName);
        StatusText.Text = $"Terminal 1: Started {title} in {repoName}";
    }

    private void LaunchRight_Click(object sender, RoutedEventArgs e)
    {
        var repo = RightRepoCombo.SelectedItem as RepositoryDefinition;
        var dir = repo?.LocalPath ?? Directory.GetCurrentDirectory();
        var repoName = repo?.Name ?? "Workspace";
        var shell = RightShellCombo.SelectedItem as ShellOption;
        var cmd = shell?.Command ?? "powershell.exe -NoLogo";
        var title = shell?.DisplayName ?? "Shell";
        TerminalRight.StartSession(cmd, dir, $"Terminal 2 · {title}", repoName);
        StatusText.Text = $"Terminal 2: Started {title} in {repoName}";
    }

    private void LaunchDualDemo_Click(object sender, RoutedEventArgs e)
    {
        ViewCockpitBtn_Click(sender, e);

        var repo1 = LeftRepoCombo.SelectedItem as RepositoryDefinition ?? _settings.Repositories.FirstOrDefault();
        var dir1 = repo1?.LocalPath ?? Directory.GetCurrentDirectory();
        var name1 = repo1?.Name ?? "Workspace 1";

        var repo2 = RightRepoCombo.SelectedItem as RepositoryDefinition ?? _settings.Repositories.Skip(1).FirstOrDefault() ?? repo1;
        var dir2 = repo2?.LocalPath ?? Directory.GetCurrentDirectory();
        var name2 = repo2?.Name ?? "Workspace 2";

        TerminalLeft.StartSession("powershell.exe -NoLogo", dir1, "Terminal 1 · PowerShell", name1);
        TerminalRight.StartSession("powershell.exe -NoLogo", dir2, "Terminal 2 · PowerShell", name2);

        StatusText.Text = "Demonstrating Stage 1: Dual concurrent ConPTY terminals launched side-by-side!";
    }

    private void ClearBothTerminals_Click(object sender, RoutedEventArgs e)
    {
        TerminalLeft.Dispose();
        TerminalRight.Dispose();
        StatusText.Text = "Terminals reset.";
    }

    protected override void OnClosed(EventArgs e)
    {
        TerminalLeft.Dispose();
        TerminalRight.Dispose();
        base.OnClosed(e);
    }
}
