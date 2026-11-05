using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AgentHub.Models;
using AgentHub.Services.Ideas;
using AgentHub.Services.Orchestrator;
using AgentHub.Services.Usage;
using AgentHub.Terminal;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;
using MessageBox = System.Windows.MessageBox;

namespace AgentHub;

public partial class IdeasWindow : Window
{
    private readonly IdeaService _ideaService;
    private readonly OrchestratorEngine _orchestrator;
    private readonly UsageService _usageService;
    private readonly Func<TerminalPaneControl?> _paneProvider;
    private readonly Action<string>? _openContextAction;
    private readonly Func<IReadOnlyList<RepositoryDefinition>> _repositories;
    private readonly Func<string, Task<RepositoryDefinition>>? _addRepository;

    private readonly ObservableCollection<ProjectIdea> _ideas = [];
    private ProjectIdea? _selectedIdea;
    private readonly DispatcherTimer _uiRefreshTimer;
    private List<OrchestratorWorkflow> _workflows = [];

    public IdeasWindow(
        IdeaService ideaService,
        OrchestratorEngine orchestrator,
        UsageService usageService,
        Func<TerminalPaneControl?> paneProvider,
        Action<string>? openContextAction = null,
        Func<IReadOnlyList<RepositoryDefinition>>? repositories = null,
        Func<string, Task<RepositoryDefinition>>? addRepository = null)
    {
        InitializeComponent();
        Controls.DarkTitleBar.Apply(this);
        Controls.WindowPlacement.KeepOnScreen(this);

        _ideaService = ideaService;
        _orchestrator = orchestrator;
        _usageService = usageService;
        _paneProvider = paneProvider;
        _openContextAction = openContextAction;
        _repositories = repositories ?? (() => []);
        _addRepository = addRepository;
        EditRepoCombo.SelectionChanged += (_, _) =>
        {
            UpdateRepoWarning();
            if (_selectedIdea != null) LoadHandoffPreview(SelectedRepositoryPath());
        };

        IdeasList.ItemsSource = _ideas;
        LogListBox.ItemsSource = _orchestrator.Logs;

        _orchestrator.StateChanged += OnOrchestratorStateChanged;
        _orchestrator.CurrentIdeaChanged += OnOrchestratorCurrentIdeaChanged;
        _orchestrator.LogAdded += _ => ScrollLogToBottom();
        _orchestrator.Steering.Changed += OnSteeringChanged;
        _orchestrator.ProgressChanged += OnOrchestratorProgress;
        _orchestrator.ThinkingChanged += OnOrchestratorThinking;

        _uiRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _uiRefreshTimer.Tick += (_, _) => RefreshStatusMetrics();
        _uiRefreshTimer.Start();

        Loaded += async (_, _) =>
        {
            try { await _orchestrator.RecoverAfterRestartAsync(); }
            catch (Exception ex) { OrchestratorSubtext.Text = "Could not tidy up after the restart: " + ex.Message; }
            await ReloadWorkflowsAsync();
            await ReloadIdeasAsync();
            UpdateOrchestratorUi();
            RefreshStatusMetrics();
        };

        Closed += (_, _) =>
        {
            _uiRefreshTimer.Stop();
            _orchestrator.StateChanged -= OnOrchestratorStateChanged;
            _orchestrator.CurrentIdeaChanged -= OnOrchestratorCurrentIdeaChanged;
            _orchestrator.Steering.Changed -= OnSteeringChanged;
            _orchestrator.ProgressChanged -= OnOrchestratorProgress;
            _orchestrator.ThinkingChanged -= OnOrchestratorThinking;
        };
    }

    private async Task ReloadIdeasAsync()
    {
        var keepId = _selectedIdea?.Id;
        var list = await _ideaService.LoadAsync();
        _ideas.Clear();
        foreach (var item in list) _ideas.Add(item);

        QueueCountText.Text = $"{_ideas.Count} idea{(_ideas.Count == 1 ? "" : "s")}";
        var keep = _ideas.FirstOrDefault(i => i.Id == keepId) ?? _ideas.FirstOrDefault();
        if (keep != null) IdeasList.SelectedItem = keep;
        else ShowEditor(false);
    }

    /// <summary>The editor works on the selected idea; with none (an empty queue) it says so instead of doing nothing.</summary>
    private void ShowEditor(bool hasIdea)
    {
        foreach (var c in new System.Windows.Controls.Control[] { EditTitleBox, EditAgentCombo, EditThresholdBox, EditRepoCombo, AddFolderBtn, NewProjectBtn, EditWorkflowCombo, WorkflowsBtn, EditDescriptionBox })
            c.IsEnabled = hasIdea;
        if (!hasIdea)
        {
            ShowRepositories(null);
            RepoWarningText.Text = "No idea selected: ➕ Add Idea first, then pick its repository.";
            RepoWarningPanel.Visibility = Visibility.Visible;
            AddUnlistedBtn.Visibility = Visibility.Collapsed;
        }
    }

    private void IdeasList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedIdea = IdeasList.SelectedItem as ProjectIdea;
        ShowEditor(_selectedIdea != null);
        if (_selectedIdea == null)
        {
            EditTitleBox.Text = string.Empty;
            EditDescriptionBox.Text = string.Empty;
            HandoffPreviewBox.Text = string.Empty;
            SteerNotesList.ItemsSource = null;
            return;
        }

        EditTitleBox.Text = _selectedIdea.Title;
        ShowRepositories(_selectedIdea.TargetDirectory);
        EditDescriptionBox.Text = _selectedIdea.Description;
        EditThresholdBox.Text = $"{_selectedIdea.ContextHandoffThreshold * 100:0}%";

        for (int i = 0; i < EditAgentCombo.Items.Count; i++)
        {
            if (EditAgentCombo.Items[i] is ComboBoxItem item &&
                string.Equals(item.Content?.ToString(), _selectedIdea.AgentCommand, StringComparison.OrdinalIgnoreCase))
            {
                EditAgentCombo.SelectedIndex = i;
                break;
            }
        }

        SelectWorkflow(_selectedIdea.WorkflowId);
        LoadHandoffPreview(_selectedIdea.TargetDirectory);
        _ = LoadSteerNotesAsync();
    }

    // ---- Workflows ----

    private async Task ReloadWorkflowsAsync()
    {
        try { _workflows = _orchestrator.Workflows is { } c ? await c.ListAsync() : []; }
        catch (Exception ex) { _workflows = []; OrchestratorSubtext.Text = "Could not read the workflow catalogue: " + ex.Message; }
        EditWorkflowCombo.ItemsSource = _workflows;
        if (_selectedIdea != null) SelectWorkflow(_selectedIdea.WorkflowId);
    }

    private void SelectWorkflow(string? id) =>
        EditWorkflowCombo.SelectedItem = _workflows.FirstOrDefault(w => w.Id == id) ?? _workflows.FirstOrDefault(w => w.Id == OrchestratorWorkflow.DefaultId);

    private async void ManageWorkflows_Click(object sender, RoutedEventArgs e)
    {
        if (_orchestrator.Workflows is not { } catalogue) return;
        var current = (EditWorkflowCombo.SelectedItem as OrchestratorWorkflow)?.Id ?? _selectedIdea?.WorkflowId;
        var window = new WorkflowsWindow(catalogue, current, canUse: _selectedIdea != null) { Owner = this };
        var used = window.ShowDialog() == true;
        await ReloadWorkflowsAsync();
        if (used && window.ChosenWorkflowId is { } chosen && _selectedIdea != null)
        {
            SelectWorkflow(chosen);
            _selectedIdea.WorkflowId = chosen;
            await _ideaService.UpdateIdeaAsync(_selectedIdea);
        }
    }

    /// <summary>The idea's workflow, or a reason it cannot run yet (inputs not filled in).</summary>
    private string? WorkflowProblem(ProjectIdea idea)
    {
        var w = _workflows.FirstOrDefault(x => x.Id == idea.WorkflowId);
        if (w == null || w.MissingInputs() is not { Count: > 0 } missing) return null;
        return w.IsTemplate
            ? $"The workflow \"{w.Name}\" is a template that needs {string.Join(", ", missing)} filled in.\n\nOpen 📚, Duplicate it, fill those in, Save, and pick your copy for this idea."
            : $"The workflow \"{w.Name}\" still needs {string.Join(", ", missing)} filled in. Open 📚 to fill it in.";
    }

    // ---- Steering ----

    private async Task LoadSteerNotesAsync()
    {
        var idea = _selectedIdea;
        SteerNotesList.ItemsSource = idea == null ? null : await _orchestrator.Steering.GetForIdeaAsync(idea.Id);
    }

    private void OnSteeringChanged(string ideaId) =>
        Dispatcher.InvokeAsync(async () =>
        {
            if (_selectedIdea?.Id != ideaId) return;
            await LoadSteerNotesAsync();
            LoadHandoffPreview(_selectedIdea?.TargetDirectory);
        });

    private void QueueSteer_Click(object sender, RoutedEventArgs e) => _ = SteerAsync(sendNow: false);

    private void SendSteerNow_Click(object sender, RoutedEventArgs e) => _ = SteerAsync(sendNow: true);

    private void SteerBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter &&
            System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Control))
        {
            e.Handled = true;
            _ = SteerAsync(sendNow: false);
        }
    }

    private async Task SteerAsync(bool sendNow)
    {
        var idea = _selectedIdea;
        var text = SteerBox.Text.Trim();
        if (idea == null || text.Length == 0) return;

        QueueSteerBtn.IsEnabled = SendSteerNowBtn.IsEnabled = false;
        try
        {
            await _orchestrator.SteerAsync(idea, text, sendNow);
            SteerBox.Clear();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not send the steer: {ex.Message}", "Steer", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            QueueSteerBtn.IsEnabled = SendSteerNowBtn.IsEnabled = true;
            await LoadSteerNotesAsync();
            LoadHandoffPreview(idea.TargetDirectory);
        }
    }

    private async void RemoveSteer_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: SteeringNote note }) return;
        // The handoff bullet stays: it is the record of what was asked, and the agent may already have read it.
        if (!await _orchestrator.Steering.RemovePendingAsync(note.Id))
            MessageBox.Show(this, "That note was already delivered.", "Steer", MessageBoxButton.OK, MessageBoxImage.Information);
        await LoadSteerNotesAsync();
    }

    private void LoadHandoffPreview(string? dir)
    {
        if (string.IsNullOrEmpty(dir))
        {
            HandoffPreviewBox.Text = "(No directory specified)";
            return;
        }

        var path = Path.Combine(dir, ".agenthub", "handoff.md");
        if (File.Exists(path))
        {
            try { HandoffPreviewBox.Text = File.ReadAllText(path); }
            catch (Exception ex) { HandoffPreviewBox.Text = $"(Error reading handoff: {ex.Message})"; }
        }
        else
        {
            HandoffPreviewBox.Text = "(No .agenthub/handoff.md file created yet. Will be generated when the build starts.)";
        }
    }

    private async void SaveIdea_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedIdea == null) return;

        _selectedIdea.Title = EditTitleBox.Text.Trim();
        _selectedIdea.TargetDirectory = SelectedRepositoryPath() ?? _selectedIdea.TargetDirectory;
        _selectedIdea.Description = EditDescriptionBox.Text.Trim();

        if (EditAgentCombo.SelectedItem is ComboBoxItem item)
        {
            _selectedIdea.AgentCommand = item.Content?.ToString() ?? "claude";
        }
        if (EditWorkflowCombo.SelectedItem is OrchestratorWorkflow workflow) _selectedIdea.WorkflowId = workflow.Id;

        var threshText = EditThresholdBox.Text.Replace("%", "").Trim();
        if (double.TryParse(threshText, out var val))
        {
            if (val > 1) val /= 100.0;
            _selectedIdea.ContextHandoffThreshold = Math.Clamp(val, 0.40, 0.95);
        }

        await _ideaService.UpdateIdeaAsync(_selectedIdea);
        await ReloadIdeasAsync();
        MessageBox.Show(this, "Idea updated successfully.", "Saved", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void AddIdea_Click(object sender, RoutedEventArgs e)
    {
        var newIdea = new ProjectIdea
        {
            Title = $"New App #{_ideas.Count + 1}",
            Description = "Describe the goal, architecture, features, and requirements for this application.",
            TargetDirectory = "",
            AgentCommand = "claude",
            ContextHandoffThreshold = 0.75,
            Status = IdeaStatus.Queued
        };

        await _ideaService.AddIdeaAsync(newIdea);
        await ReloadIdeasAsync();
        IdeasList.SelectedItem = _ideas.FirstOrDefault(i => i.Id == newIdea.Id);
    }

    private async void DeleteIdea_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not ProjectIdea idea) return;

        var result = MessageBox.Show(this,
            $"Are you sure you want to delete idea \"{idea.Title}\" from the queue?",
            "Confirm Delete",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
        {
            await _ideaService.DeleteIdeaAsync(idea.Id);
            await ReloadIdeasAsync();
        }
    }

    private async void MoveUp_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not ProjectIdea idea) return;
        var idx = _ideas.IndexOf(idea);
        if (idx > 0)
        {
            await _ideaService.ReorderAsync(idea.Id, idx - 1);
            await ReloadIdeasAsync();
            IdeasList.SelectedIndex = idx - 1;
        }
    }

    private async void MoveDown_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not ProjectIdea idea) return;
        var idx = _ideas.IndexOf(idea);
        if (idx >= 0 && idx < _ideas.Count - 1)
        {
            await _ideaService.ReorderAsync(idea.Id, idx + 1);
            await ReloadIdeasAsync();
            IdeasList.SelectedIndex = idx + 1;
        }
    }

    private async void BuildNow_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not ProjectIdea idea) return;

        if (WorkflowProblem(idea) is { } problem)
        {
            MessageBox.Show(this, problem, "Workflow", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (string.IsNullOrWhiteSpace(idea.TargetDirectory))
        {
            MessageBox.Show(this, "Pick the repository this idea is built in (or ➕ New project), then Save.", "Repository", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var pane = _paneProvider();
        if (pane == null)
        {
            MessageBox.Show(this, "Every Cockpit pane has a running session. Close one (or end its session) so the builder gets a pane of its own.", "Cockpit", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        await _orchestrator.BuildIdeaImmediatelyAsync(idea, pane);
        UpdateOrchestratorUi();
    }

    private async void ToggleOrchestrator_Click(object sender, RoutedEventArgs e)
    {
        if (_orchestrator.State is OrchestratorState.Building or OrchestratorState.HandoffPending or OrchestratorState.WaitingForReset)
        {
            _orchestrator.Pause();
        }
        else if (_orchestrator.State == OrchestratorState.Paused)
        {
            await _orchestrator.ResumeAsync();
        }
        else
        {
            var pane = _paneProvider();
            if (pane == null)
            {
                MessageBox.Show(this, "Every Cockpit pane has a running session. Close one (or end its session) so the builder gets a pane of its own.", "Cockpit", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            await _orchestrator.StartAsync(pane);
        }

        UpdateOrchestratorUi();
    }

    private async void SyncGitHub_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(this,
            "This will fetch the latest commits from origin/main, fast-forward pull the local repository on Desktop, and compile AgentHub. Continue?",
            "Sync with GitHub",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes) return;

        var (succeeded, msg) = await _orchestrator.SyncAgentHubFromGitHubAsync(progress: text =>
        {
            Dispatcher.Invoke(() => OrchestratorSubtext.Text = text);
        });

        MessageBox.Show(this, msg, succeeded ? "Sync Complete" : "Sync Failed", MessageBoxButton.OK,
            succeeded ? MessageBoxImage.Information : MessageBoxImage.Warning);

        OrchestratorSubtext.Text = "Maintain your app ideas, orchestrate background building while away, auto-wait on Claude usage resets & handoff.";
    }

    // ---- Repository ----

    /// <summary>Marks the idea's folder when it is not one of AgentHub's repositories (shown in the list, flagged, with a one-click add).</summary>
    public sealed class UnlistedFolder
    {
        public required string LocalPath { get; init; }
        public string Name => "⚠ " + (Path.GetFileName(LocalPath.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } n ? n : LocalPath);
        public override string ToString() => Name;
    }

    private static bool SamePath(string a, string b)
    {
        try { return string.Equals(Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase); }
        catch { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
    }

    private void ShowRepositories(string? selectPath)
    {
        var items = new List<object>(_repositories());
        object? selected = null;
        if (!string.IsNullOrWhiteSpace(selectPath))
        {
            selected = items.OfType<RepositoryDefinition>().FirstOrDefault(r => SamePath(r.LocalPath, selectPath));
            if (selected == null)
            {
                selected = new UnlistedFolder { LocalPath = selectPath };
                items.Insert(0, selected);
            }
        }
        EditRepoCombo.ItemsSource = items;
        EditRepoCombo.SelectedItem = selected;
        UpdateRepoWarning();
    }

    private string? SelectedRepositoryPath() => EditRepoCombo.SelectedItem switch
    {
        RepositoryDefinition r => r.LocalPath,
        UnlistedFolder u => u.LocalPath,
        _ => null
    };

    private void UpdateRepoWarning()
    {
        string? warning = EditRepoCombo.SelectedItem switch
        {
            UnlistedFolder u => $"{u.LocalPath} is not one of AgentHub's repositories.",
            RepositoryDefinition { ExistsOnDisk: false } r => $"{r.LocalPath} is missing on disk.",
            null when _selectedIdea != null => "No repository picked yet.",
            _ => null
        };
        RepoWarningText.Text = warning ?? "";
        RepoWarningPanel.Visibility = warning == null ? Visibility.Collapsed : Visibility.Visible;
        AddUnlistedBtn.Visibility = EditRepoCombo.SelectedItem is UnlistedFolder ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Adds a folder to AgentHub's repositories, selects it, and saves it on the idea.</summary>
    private async Task UseFolderAsync(string path)
    {
        if (_addRepository == null) return;
        try
        {
            var repo = await _addRepository(path);
            ShowRepositories(repo.LocalPath);
            if (_selectedIdea != null)
            {
                _selectedIdea.TargetDirectory = repo.LocalPath;
                await _ideaService.UpdateIdeaAsync(_selectedIdea);
                LoadHandoffPreview(repo.LocalPath);
                OrchestratorSubtext.Text = $"\"{_selectedIdea.Title}\" is built in {repo.Name} ({repo.LocalPath}).";
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Repository", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void AddCurrentFolderAsRepo_Click(object sender, RoutedEventArgs e)
    {
        if (EditRepoCombo.SelectedItem is UnlistedFolder u) await UseFolderAsync(u.LocalPath);
    }

    private async void BrowseTargetDir_Click(object sender, RoutedEventArgs e)
    {
        var current = SelectedRepositoryPath();
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Pick the folder this idea is built in; it is added to AgentHub's repositories",
            UseDescriptionForTitle = true,
            InitialDirectory = current != null && Directory.Exists(current) ? current : Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
        };
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK) await UseFolderAsync(dialog.SelectedPath);
    }

    /// <summary>Where ➕ New project creates folders (AGENTHUB_PROJECTS_DIR for test copies).</summary>
    public static string ProjectsFolder =>
        Environment.GetEnvironmentVariable("AGENTHUB_PROJECTS_DIR") is { Length: > 0 } d ? d
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Desktop", "ClaudeCProjects");

    private async void NewProjectRepo_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedIdea == null)
        {
            MessageBox.Show(this, "Add an idea first (➕ Add Idea), then make its new project.", "New project", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var suggested = string.Join("", (EditTitleBox.Text.Trim() is { Length: > 0 } t ? t : _selectedIdea.Title).Split(Path.GetInvalidFileNameChars())).Trim();
        var name = Microsoft.VisualBasic.Interaction.InputBox(
            $"Name of the new project folder (created in {ProjectsFolder}, git init, added to AgentHub's repositories):",
            "New project", suggested);
        name = string.Join("", name.Split(Path.GetInvalidFileNameChars())).Trim();
        if (name.Length == 0) return;
        var path = Path.Combine(ProjectsFolder, name);
        if (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any() &&
            MessageBox.Show(this, $"{path} already exists and is not empty. Use it anyway?", "New project", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        await UseFolderAsync(path);
    }

    private void OpenHandoffInContext_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedIdea == null || string.IsNullOrEmpty(_selectedIdea.TargetDirectory)) return;
        _openContextAction?.Invoke(_selectedIdea.TargetDirectory);
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e) => _orchestrator.Logs.Clear();

    private void OnOrchestratorProgress(string progress) =>
        Dispatcher.InvokeAsync(() => { if (!_orchestrator.IsThinking) OrchestratorProgressLabel.Text = progress; });

    private void OnOrchestratorThinking(bool thinking) =>
        Dispatcher.InvokeAsync(() => OrchestratorProgressLabel.Text = thinking
            ? "thinking…" + (_orchestrator.LatestProgress is { } p ? "  (last: " + p + ")" : "")
            : _orchestrator.LatestProgress ?? "Waiting for the builder");

    /// <summary>Opens the run archive: the repository branch's INDEX.md when it exists, else the local runs folder.</summary>
    private void OpenRuns_Click(object sender, RoutedEventArgs e)
    {
        var index = _orchestrator.Archive is { } p ? Path.Combine(p.RunsPath, "INDEX.md") : null;
        var target = index != null && File.Exists(index) ? index : _orchestrator.RunsRoot;
        try
        {
            Directory.CreateDirectory(_orchestrator.RunsRoot);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not open {target}: {ex.Message}", "Runs", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnOrchestratorStateChanged(OrchestratorState state) =>
        Dispatcher.Invoke(() =>
        {
            UpdateOrchestratorUi();
            _ = ReloadIdeasAsync();
        });

    private void OnOrchestratorCurrentIdeaChanged(ProjectIdea? idea) =>
        Dispatcher.Invoke(() =>
        {
            ActiveIdeaLabel.Text = idea != null ? $"{idea.Title} (Session #{idea.HandoffCount + 1})" : "None (Idle)";
            _ = ReloadIdeasAsync();
        });

    private void UpdateOrchestratorUi()
    {
        switch (_orchestrator.State)
        {
            case OrchestratorState.Building:
                OrchestratorStatusBadge.Background = new SolidColorBrush(Color.FromRgb(16, 185, 129)); // Green
                OrchestratorStatusText.Text = "BUILDING";
                OrchestratorStatusText.Foreground = Brushes.White;
                ToggleOrchestratorBtn.Content = "⏸ Pause Auto-Builder";
                ToggleOrchestratorBtn.Background = new SolidColorBrush(Color.FromRgb(245, 158, 11)); // Amber
                break;

            case OrchestratorState.HandoffPending:
                OrchestratorStatusBadge.Background = new SolidColorBrush(Color.FromRgb(139, 92, 246)); // Purple
                OrchestratorStatusText.Text = "HANDOFF CYCLE";
                OrchestratorStatusText.Foreground = Brushes.White;
                ToggleOrchestratorBtn.Content = "⏸ Pause Auto-Builder";
                ToggleOrchestratorBtn.Background = new SolidColorBrush(Color.FromRgb(245, 158, 11));
                break;

            case OrchestratorState.WaitingForReset:
                OrchestratorStatusBadge.Background = new SolidColorBrush(Color.FromRgb(245, 158, 11)); // Amber
                OrchestratorStatusText.Text = "WAITING FOR RESET";
                OrchestratorStatusText.Foreground = Brushes.Black;
                ToggleOrchestratorBtn.Content = "⏸ Pause Auto-Builder";
                ToggleOrchestratorBtn.Background = new SolidColorBrush(Color.FromRgb(245, 158, 11));
                break;

            case OrchestratorState.Paused:
                OrchestratorStatusBadge.Background = new SolidColorBrush(Color.FromRgb(107, 114, 128)); // Gray
                OrchestratorStatusText.Text = "PAUSED";
                OrchestratorStatusText.Foreground = Brushes.White;
                ToggleOrchestratorBtn.Content = "▶ Resume Auto-Builder";
                ToggleOrchestratorBtn.Background = new SolidColorBrush(Color.FromRgb(16, 185, 129));
                break;

            default:
                OrchestratorStatusBadge.Background = new SolidColorBrush(Color.FromRgb(30, 41, 59)); // Slate
                OrchestratorStatusText.Text = "IDLE";
                OrchestratorStatusText.Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184));
                ToggleOrchestratorBtn.Content = "▶ Start Auto-Builder (Work Mode)";
                ToggleOrchestratorBtn.Background = new SolidColorBrush(Color.FromRgb(16, 185, 129));
                break;
        }

        ActiveIdeaLabel.Text = _orchestrator.CurrentIdea != null
            ? $"{_orchestrator.CurrentIdea.Title} (Session #{_orchestrator.CurrentIdea.HandoffCount + 1})"
            : "None (Idle)";
    }

    private async void RefreshStatusMetrics()
    {
        try
        {
            var provider = new UsageProviderDefinition
            {
                Id = "claude",
                Name = "Claude Code",
                Command = "claude"
            };

            var snapshot = await ClaudeUsageReader.ReadAsync(provider);
            if (snapshot.Status == UsageCollectionStatus.Available && snapshot.Limits.Count > 0)
            {
                var summary = string.Join(" · ", snapshot.Limits.Select(l =>
                    $"{l.Name}: {l.RemainingFraction * 100:0}%"));
                ClaudeQuotaLabel.Text = summary;
            }
            else
            {
                ClaudeQuotaLabel.Text = snapshot.Error ?? "Claude CLI not reporting live usage.";
            }

            if (_orchestrator.NextResetTime.HasValue)
            {
                var duration = _orchestrator.NextResetTime.Value - DateTimeOffset.Now;
                if (duration.TotalSeconds > 0)
                {
                    ResetCountdownLabel.Text = $"Resets at {_orchestrator.NextResetTime:t} (in {(int)duration.TotalHours}h {duration.Minutes}m {duration.Seconds}s)";
                }
                else
                {
                    ResetCountdownLabel.Text = "Reset time reached. Resuming...";
                }
            }
            else
            {
                ResetCountdownLabel.Text = "No quota pause active (OK)";
            }
        }
        catch { }
    }

    private void ScrollLogToBottom()
    {
        Dispatcher.Invoke(() =>
        {
            if (LogListBox.Items.Count > 0)
            {
                LogListBox.ScrollIntoView(LogListBox.Items[^1]);
            }
        });
    }
}
