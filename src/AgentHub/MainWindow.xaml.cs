using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using AgentHub.Models;
using AgentHub.Services;
using Forms = System.Windows.Forms;
using MessageBox = System.Windows.MessageBox;

namespace AgentHub;

public partial class MainWindow : Window
{
    private readonly SettingsService _settingsService = new();
    private readonly ProcessRunner _runner = new();
    private readonly GitService _git;
    private readonly AgentLauncher _agentLauncher = new();
    private readonly RepoContextService _repoContext = new();
    private AppSettings _settings = new();
    private RepositoryDefinition? _selectedRepo;

    public MainWindow()
    {
        InitializeComponent();
        _git = new GitService(_runner);
        Loaded += MainWindow_Loaded;
        AgentCombo.SelectionChanged += (_, _) => UpdateAgentPreview();
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _settings = await _settingsService.LoadAsync();
        RefreshRepoList();
        RefreshAgentList();
        StatusText.Text = $"Settings: {_settingsService.SettingsPath}";
    }

    private void RefreshRepoList()
    {
        var selectedId = _selectedRepo?.Id;
        RepoList.ItemsSource = null;
        RepoList.ItemsSource = _settings.Repositories.OrderBy(r => r.Name).ToList();
        if (selectedId is not null)
            RepoList.SelectedItem = _settings.Repositories.FirstOrDefault(r => r.Id == selectedId);
    }

    private void RefreshAgentList()
    {
        AgentCombo.ItemsSource = _settings.Agents.Where(a => a.Enabled).ToList();
        if (AgentCombo.Items.Count > 0) AgentCombo.SelectedIndex = 0;
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
}
