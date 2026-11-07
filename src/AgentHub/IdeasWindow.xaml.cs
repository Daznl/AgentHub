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
    private readonly OrchestratorFleet _fleet;
    private readonly UsageService _usageService;
    /// <summary>A pane for an engine's worker (null engine: a new one); null when every pane is busy.</summary>
    private readonly Func<OrchestratorEngine?, TerminalPaneControl?> _paneProvider;
    /// <summary>The orchestration shown in the monitor: the selected idea's, null when it has none.</summary>
    private OrchestratorEngine? _shown;
    private readonly ObservableCollection<OrchestratorExchange> _conversation = [];
    private readonly Action<string>? _openContextAction;
    private readonly Func<IReadOnlyList<RepositoryDefinition>> _repositories;
    private readonly Func<string, Task<RepositoryDefinition>>? _addRepository;

    private readonly ObservableCollection<ProjectIdea> _ideas = [];
    private ProjectIdea? _selectedIdea;
    private readonly DispatcherTimer _uiRefreshTimer;
    private List<OrchestratorWorkflow> _workflows = [];

    public IdeasWindow(
        IdeaService ideaService,
        OrchestratorFleet orchestrators,
        UsageService usageService,
        Func<OrchestratorEngine?, TerminalPaneControl?> paneProvider,
        Action<string>? openContextAction = null,
        Func<IReadOnlyList<RepositoryDefinition>>? repositories = null,
        Func<string, Task<RepositoryDefinition>>? addRepository = null)
    {
        InitializeComponent();
        Controls.DarkTitleBar.Apply(this);
        Controls.WindowPlacement.KeepOnScreen(this);

        _ideaService = ideaService;
        _fleet = orchestrators;
        _usageService = usageService;
        _paneProvider = paneProvider;
        _openContextAction = openContextAction;
        _repositories = repositories ?? (() => []);
        _addRepository = addRepository;
        // Inside a ScrollViewer the star row would grow to the whole handoff text. Give the editor a definite height instead: the visible
        // height, or what its fixed parts need plus a 180px handoff box when the window is shorter (then it scrolls).
        EditorScroll.SizeChanged += (_, _) => FitEditor();
        EditorGrid.SizeChanged += (_, e) => { if (e.WidthChanged) FitEditor(); };
        EditRepoCombo.SelectionChanged += (_, _) =>
        {
            UpdateRepoWarning();
            if (_selectedIdea != null) LoadHandoffPreview(SelectedRepositoryPath());
        };

        IdeasList.ItemsSource = _ideas;
        LogListBox.ItemsSource = _fleet.Logs;
        ConversationList.ItemsSource = _conversation;

        _fleet.StateChanged += OnFleetStateChanged;
        _fleet.Changed += OnFleetChanged;
        _fleet.LogAdded += OnFleetLog;
        _fleet.Steering.Changed += OnSteeringChanged;

        _uiRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _uiRefreshTimer.Tick += (_, _) => RefreshStatusMetrics();
        _uiRefreshTimer.Start();

        Loaded += async (_, _) =>
        {
            try { await _fleet.RecoverAfterRestartAsync(); }
            catch (Exception ex) { OrchestratorSubtext.Text = "Could not tidy up after the restart: " + ex.Message; }
            await ReloadWorkflowsAsync();
            await ReloadIdeasAsync();
            UpdateOrchestratorUi();
            ShowSelectedRun();
            RefreshStatusMetrics();
        };

        Closed += (_, _) =>
        {
            _uiRefreshTimer.Stop();
            _fleet.StateChanged -= OnFleetStateChanged;
            _fleet.Changed -= OnFleetChanged;
            _fleet.LogAdded -= OnFleetLog;
            _fleet.Steering.Changed -= OnSteeringChanged;
        };
    }

    private async Task ReloadIdeasAsync()
    {
        await SaveEditorAsync();
        var keepId = _selectedIdea?.Id;
        var list = await _ideaService.LoadAsync();
        _reloading = true;
        try
        {
            _ideas.Clear();
            foreach (var item in list) _ideas.Add(item);
        }
        finally { _reloading = false; }

        QueueCountText.Text = $"{_ideas.Count} idea{(_ideas.Count == 1 ? "" : "s")}";
        var keep = _ideas.FirstOrDefault(i => i.Id == keepId) ?? _ideas.FirstOrDefault();
        if (keep != null) IdeasList.SelectedItem = keep;
        else ShowEditor(false);
    }

    private void FitEditor()
    {
        var handoffRow = EditorGrid.RowDefinitions[5];
        double fixedParts = 0;
        foreach (UIElement child in EditorGrid.Children)
        {
            if (Grid.GetRow(child) == 5) continue;
            child.Measure(new System.Windows.Size(Math.Max(0, EditorGrid.ActualWidth), double.PositiveInfinity));
        }
        for (var r = 0; r < 5; r++)
            fixedParts += EditorGrid.RowDefinitions[r].Height.IsAbsolute
                ? EditorGrid.RowDefinitions[r].Height.Value
                : EditorGrid.Children.Cast<UIElement>().Where(c => Grid.GetRow(c) == r).Select(c => c.DesiredSize.Height).DefaultIfEmpty(0).Max();
        var needed = fixedParts + handoffRow.MinHeight + EditorGrid.Margin.Top + EditorGrid.Margin.Bottom;
        var height = Math.Max(EditorScroll.ActualHeight, needed);
        if (Math.Abs(EditorGrid.Height - height) > 0.5 || double.IsNaN(EditorGrid.Height)) EditorGrid.Height = height;
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
        // Switching to another idea keeps what was typed for the one being left.
        if (!_reloading && _selectedIdea is { } leaving && e.RemovedItems.Contains(leaving) && EditTitleBox.IsEnabled && EditorDiffersFrom(leaving))
        {
            ApplyEditorTo(leaving);
            _ = SaveFieldsAsync(leaving);
        }
        _selectedIdea = IdeasList.SelectedItem as ProjectIdea;
        ShowEditor(_selectedIdea != null);
        ShowSelectedRun();
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

        var agentFound = false;
        for (int i = 0; i < EditAgentCombo.Items.Count; i++)
        {
            if (EditAgentCombo.Items[i] is ComboBoxItem item &&
                string.Equals(item.Content?.ToString(), _selectedIdea.AgentCommand, StringComparison.OrdinalIgnoreCase))
            {
                EditAgentCombo.SelectedIndex = i;
                agentFound = true;
                break;
            }
        }
        // A command not in the list (set in ideas.json) is shown and kept; picking the first item would silently replace it
        // with claude on the next save.
        if (!agentFound && !string.IsNullOrWhiteSpace(_selectedIdea.AgentCommand))
            EditAgentCombo.SelectedIndex = EditAgentCombo.Items.Add(new ComboBoxItem { Content = _selectedIdea.AgentCommand });

        SelectWorkflow(_selectedIdea.WorkflowId);
        LoadHandoffPreview(HandoffFolder(_selectedIdea));
        _ = LoadSteerNotesAsync();
    }

    // ---- Workflows ----

    private async Task ReloadWorkflowsAsync()
    {
        try { _workflows = _fleet.Workflows is { } c ? await c.ListAsync() : []; }
        catch (Exception ex) { _workflows = []; OrchestratorSubtext.Text = "Could not read the workflow catalogue: " + ex.Message; }
        EditWorkflowCombo.ItemsSource = _workflows;
        if (_selectedIdea != null) SelectWorkflow(_selectedIdea.WorkflowId);
    }

    private void SelectWorkflow(string? id) =>
        EditWorkflowCombo.SelectedItem = _workflows.FirstOrDefault(w => w.Id == id) ?? _workflows.FirstOrDefault(w => w.Id == OrchestratorWorkflow.DefaultId);

    private async void ManageWorkflows_Click(object sender, RoutedEventArgs e)
    {
        if (_fleet.Workflows is not { } catalogue) return;
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
        SteerNotesList.ItemsSource = idea == null ? null : await _fleet.Steering.GetForIdeaAsync(idea.Id);
    }

    private void OnSteeringChanged(string ideaId) =>
        Dispatcher.InvokeAsync(async () =>
        {
            if (_selectedIdea?.Id != ideaId) return;
            await LoadSteerNotesAsync();
            LoadHandoffPreview(_selectedIdea is { } shown ? HandoffFolder(shown) : null);
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
            await _fleet.SteerAsync(idea, text, sendNow);
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
            LoadHandoffPreview(HandoffFolder(idea));
        }
    }

    private async void RemoveSteer_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: SteeringNote note }) return;
        // The handoff bullet stays: it is the record of what was asked, and the agent may already have read it.
        if (!await _fleet.Steering.RemovePendingAsync(note.Id))
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
        await SaveEditorAsync(force: true);
        await ReloadIdeasAsync();
        MessageBox.Show(this, "Idea updated successfully.", "Saved", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>The editor's fields onto an idea.</summary>
    private void ApplyEditorTo(ProjectIdea idea)
    {
        idea.Title = EditTitleBox.Text.Trim();
        idea.TargetDirectory = SelectedRepositoryPath() ?? idea.TargetDirectory;
        idea.Description = EditDescriptionBox.Text.Trim();
        if (EditAgentCombo.SelectedItem is ComboBoxItem item) idea.AgentCommand = item.Content?.ToString() ?? "claude";
        if (EditWorkflowCombo.SelectedItem is OrchestratorWorkflow workflow) idea.WorkflowId = workflow.Id;
        var threshText = EditThresholdBox.Text.Replace("%", "").Trim();
        if (double.TryParse(threshText, out var val))
        {
            if (val > 1) val /= 100.0;
            idea.ContextHandoffThreshold = Math.Clamp(val, 0.40, 0.95);
        }
    }

    /// <summary>True when the editor shows something other than what is saved for the idea (typed, not yet saved).</summary>
    private bool EditorDiffersFrom(ProjectIdea idea)
    {
        var probe = new ProjectIdea
        {
            Title = idea.Title, TargetDirectory = idea.TargetDirectory, Description = idea.Description, AgentCommand = idea.AgentCommand,
            WorkflowId = idea.WorkflowId, ContextHandoffThreshold = idea.ContextHandoffThreshold
        };
        ApplyEditorTo(probe);
        return probe.Title != idea.Title || probe.TargetDirectory != idea.TargetDirectory || probe.Description != idea.Description.Trim()
            || probe.AgentCommand != idea.AgentCommand || probe.WorkflowId != idea.WorkflowId || Math.Abs(probe.ContextHandoffThreshold - idea.ContextHandoffThreshold) > 0.001;
    }

    /// <summary>
    /// Saves what is typed in the editor for the selected idea. Build Now, Start, switching ideas and every reload call this, so
    /// a spec typed but not saved with 💾 is never lost or skipped (owner feedback 2026-11-05: a paragraph typed, then Build Now built the placeholder).
    /// </summary>
    private async Task<bool> SaveEditorAsync(bool force = false)
    {
        if (_selectedIdea is not { } idea || !EditTitleBox.IsEnabled) return false;
        if (!force && !EditorDiffersFrom(idea)) return false;
        ApplyEditorTo(idea);
        await SaveFieldsAsync(idea);
        return true;
    }

    /// <summary>Writes the editor's fields onto the idea as saved now, so a status the engine just set is not overwritten by an older copy.</summary>
    private async Task SaveFieldsAsync(ProjectIdea edited)
    {
        var current = (await _ideaService.LoadAsync()).FirstOrDefault(i => i.Id == edited.Id);
        if (current == null) return;
        current.Title = edited.Title;
        current.TargetDirectory = edited.TargetDirectory;
        current.Description = edited.Description;
        current.AgentCommand = edited.AgentCommand;
        current.WorkflowId = edited.WorkflowId;
        current.ContextHandoffThreshold = edited.ContextHandoffThreshold;
        await _ideaService.UpdateIdeaAsync(current);
    }

    private bool _reloading;

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

        if (_selectedIdea?.Id == idea.Id && await SaveEditorAsync())
            idea = (await _ideaService.LoadAsync()).FirstOrDefault(i => i.Id == idea.Id) ?? idea;

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

        // Its own orchestrator, beside the others: never suspends another idea's run (owner feedback 2026-11-06).
        if (_selectedIdea?.Id != idea.Id) IdeasList.SelectedItem = _ideas.FirstOrDefault(i => i.Id == idea.Id);
        try { await _fleet.BuildNowAsync(idea, _paneProvider); }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show(this, ex.Message, "Cockpit", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        UpdateOrchestratorUi();
        ShowSelectedRun();
    }

    /// <summary>The Auto-Builder lane: one more orchestrator, working through the queue one idea after another.</summary>
    private async void ToggleOrchestrator_Click(object sender, RoutedEventArgs e)
    {
        var auto = _fleet.AutoBuilder;
        if (auto?.State is OrchestratorState.Building or OrchestratorState.HandoffPending or OrchestratorState.WaitingForReset)
            auto.Pause();
        else
        {
            await SaveEditorAsync();
            try { await _fleet.StartAutoBuilderAsync(_paneProvider); }
            catch (InvalidOperationException ex)
            {
                MessageBox.Show(this, ex.Message, "Cockpit", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }
        UpdateOrchestratorUi();
        ShowSelectedRun();
    }

    /// <summary>Pause or Resume the selected idea's orchestration only.</summary>
    private async void SelectedPause_Click(object sender, RoutedEventArgs e)
    {
        if (_shown is not { } engine || engine.State == OrchestratorState.Idle) return;
        if (engine.State == OrchestratorState.Paused)
        {
            await SaveEditorAsync();
            await engine.ResumeAsync();
        }
        else engine.Pause();
        UpdateOrchestratorUi();
        ShowSelectedRun();
    }

    /// <summary>Stops the selected idea's orchestration and closes its run; the others carry on.</summary>
    private void SelectedStop_Click(object sender, RoutedEventArgs e)
    {
        if (_shown is not { } engine || engine.State == OrchestratorState.Idle) return;
        var title = engine.CurrentIdea?.Title ?? "this idea";
        if (MessageBox.Show(this, $"Stop the orchestration of \"{title}\"? Its run is closed; its worker is left as it is. The other orchestrations carry on.",
                "Stop", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        engine.Stop();
        UpdateOrchestratorUi();
        ShowSelectedRun();
    }

    private async void SyncGitHub_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(this,
            "This will fetch the latest commits from origin/main, fast-forward pull the local repository on Desktop, and compile AgentHub. Continue?",
            "Sync with GitHub",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes) return;

        var (succeeded, msg) = await _fleet.SyncAgentHubFromGitHubAsync(progress: text =>
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

    private void ClearLog_Click(object sender, RoutedEventArgs e) => (_shown?.Logs ?? _fleet.Logs).Clear();

    /// <summary>The folder whose handoff the editor previews: the run's worktree while it has one, else the repository.</summary>
    private string? HandoffFolder(ProjectIdea idea) => _fleet.EngineFor(idea.Id)?.WorkingDirectory ?? idea.TargetDirectory;

    private void OnFleetStateChanged() =>
        Dispatcher.InvokeAsync(() =>
        {
            UpdateOrchestratorUi();
            ShowSelectedRun();
            _ = ReloadIdeasAsync();
        });

    private void OnFleetChanged() => Dispatcher.InvokeAsync(ShowSelectedRun);

    private void OnFleetLog(OrchestratorEngine engine, OrchestratorLog entry)
    {
        if (engine == _shown) ScrollLogToBottom();
    }

    /// <summary>
    /// The monitor shows the orchestration of the idea selected on the left (owner feedback 2026-11-06): its state, folder, controls,
    /// progress, conversation and log. An idea with none shows AgentHub's own notes in the log.
    /// </summary>
    private void ShowSelectedRun()
    {
        var engine = _selectedIdea is { } idea ? _fleet.EngineFor(idea.Id) : null;
        if (engine != _shown)
        {
            _shown = engine;
            LogListBox.ItemsSource = engine?.Logs ?? _fleet.Logs;
            _conversation.Clear();
            ScrollLogToBottom();
        }

        var state = engine?.State ?? OrchestratorState.Idle;
        var running = state != OrchestratorState.Idle;
        var (label, background, foreground) = state switch
        {
            OrchestratorState.Paused => ("PAUSED", Color.FromRgb(107, 114, 128), Colors.White),
            OrchestratorState.WaitingForReset => ("WAITING FOR RESET", Color.FromRgb(245, 158, 11), Colors.Black),
            OrchestratorState.HandoffPending => ("HANDING OFF", Color.FromRgb(139, 92, 246), Colors.White),
            OrchestratorState.Building when engine!.IsThinking => ("THINKING…", Color.FromRgb(124, 58, 237), Colors.White),
            OrchestratorState.Building => ("DRIVING", Color.FromRgb(16, 185, 129), Colors.White),
            _ => ("NOT RUNNING", Color.FromRgb(30, 41, 59), Color.FromRgb(148, 163, 184)),
        };
        SelectedStateText.Text = label;
        SelectedStateText.Foreground = new SolidColorBrush(foreground);
        SelectedStateChip.Background = new SolidColorBrush(background);

        SelectedIdeaLabel.Text = _selectedIdea == null ? "No idea selected"
            : _selectedIdea.Title + (running && engine!.WorkerSession > 0 ? $" · worker #{engine.WorkerSession}" : "")
              + (engine?.FollowsQueue == true && running ? " · Auto-Builder" : "");
        SelectedFolderLabel.Text = engine?.WorkingDirectory is { } wd
            ? engine.WorktreeBranch is { } branch ? $"Worktree {wd} (branch {branch})" : wd
            : _selectedIdea?.TargetDirectory ?? "";
        SelectedFolderLabel.ToolTip = string.IsNullOrEmpty(SelectedFolderLabel.Text) ? null : SelectedFolderLabel.Text;

        SelectedPauseBtn.IsEnabled = SelectedStopBtn.IsEnabled = running;
        SelectedPauseBtn.Content = state == OrchestratorState.Paused ? "▶ Resume" : "⏸ Pause";

        OrchestratorProgressLabel.Text = !running ? (engine?.LatestProgress is { } last ? "Not running · last: " + last : "Not running")
            : engine!.IsThinking ? "thinking…" + (engine.LatestProgress is { } p ? "  (last: " + p + ")" : "")
            : engine.LatestProgress ?? "Waiting for the builder";
        OrchestratorProgressLabel.ToolTip = OrchestratorProgressLabel.Text;

        var next = engine?.Conversation ?? [];
        var scroll = ConversationScroll;
        var following = scroll.VerticalOffset >= scroll.ScrollableHeight - 8 || _conversation.Count == 0
            || next.Count == 0 || !ReferenceEquals(_conversation[0], next[0]);
        if (OrchestratorBoxView.Sync(_conversation, next) && following)
            Dispatcher.BeginInvoke(scroll.ScrollToEnd, DispatcherPriority.Loaded);
        ConversationEmptyText.Text = engine == null ? "This idea has no orchestration yet: ▶ Build Now starts one beside any others."
            : next.Count == 0 ? OrchestratorBoxView.NothingYet : "";
        ConversationEmptyText.Visibility = next.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        LogHeaderText.Text = engine == null ? "Activity Log (AgentHub)" : "Activity Log";
    }

    /// <summary>Opens the runs index (INDEX.md in the runs folder) when there is one, else the runs folder.</summary>
    private void OpenRuns_Click(object sender, RoutedEventArgs e)
    {
        var index = Path.Combine(_fleet.RunsRoot, "INDEX.md");
        var target = File.Exists(index) ? index : _fleet.RunsRoot;
        try
        {
            Directory.CreateDirectory(_fleet.RunsRoot);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not open {target}: {ex.Message}", "Runs", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>The top bar: how many orchestrations are going, and the Auto-Builder lane's button.</summary>
    private void UpdateOrchestratorUi()
    {
        var active = _fleet.Active;
        var paused = active.Count(e => e.State == OrchestratorState.Paused);
        var going = active.Count - paused;
        OrchestratorStatusText.Text = active.Count == 0 ? "IDLE"
            : going == 0 ? $"{paused} PAUSED"
            : $"{going} RUNNING" + (paused > 0 ? $" · {paused} PAUSED" : "");
        OrchestratorStatusBadge.Background = new SolidColorBrush(active.Count == 0 ? Color.FromRgb(30, 41, 59)
            : going == 0 ? Color.FromRgb(107, 114, 128) : Color.FromRgb(16, 185, 129));
        OrchestratorStatusText.Foreground = active.Count == 0 ? new SolidColorBrush(Color.FromRgb(148, 163, 184)) : Brushes.White;

        var auto = _fleet.AutoBuilder?.State ?? OrchestratorState.Idle;
        var (text, colour) = auto switch
        {
            OrchestratorState.Building or OrchestratorState.HandoffPending or OrchestratorState.WaitingForReset => ("⏸ Pause Auto-Builder", Color.FromRgb(245, 158, 11)),
            OrchestratorState.Paused => ("▶ Resume Auto-Builder", Color.FromRgb(16, 185, 129)),
            _ => ("▶ Start Auto-Builder (Work Mode)", Color.FromRgb(16, 185, 129)),
        };
        ToggleOrchestratorBtn.Content = text;
        ToggleOrchestratorBtn.Background = new SolidColorBrush(colour);
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

            // One Claude account: the selected run's reset, else any run's.
            var reset = _shown?.NextResetTime ?? _fleet.Active.Select(e => e.NextResetTime).FirstOrDefault(t => t.HasValue);
            if (reset.HasValue)
            {
                var duration = reset.Value - DateTimeOffset.Now;
                ResetCountdownLabel.Text = duration.TotalSeconds > 0
                    ? $"Resets at {reset:t} (in {(int)duration.TotalHours}h {duration.Minutes}m {duration.Seconds}s)"
                    : "Reset time reached. Resuming...";
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
        Dispatcher.InvokeAsync(() =>
        {
            if (LogListBox.Items.Count > 0)
            {
                LogListBox.ScrollIntoView(LogListBox.Items[^1]);
            }
        });
    }
}
