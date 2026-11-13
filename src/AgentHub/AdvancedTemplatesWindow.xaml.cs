using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using AgentHub.Services.Orchestrator;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using Cursors = System.Windows.Input.Cursors;
using Color = System.Windows.Media.Color;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using MessageBox = System.Windows.MessageBox;
using Path = System.IO.Path;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace AgentHub;

/// <summary>
/// 🗺 Advanced templates: a project's advanced templates (<c>orchestration/*.json</c> in its repository), each drawn as the
/// tree an orchestrator follows: stages, and under each its steps, top to bottom in the order a single worker takes them.
/// When an orchestrator on a pane follows one (<see cref="BindRun"/>), its tree is live: every step shows how far the run
/// has got, and the run is started, paused and sent on from any step here. ✏ Edit changes a template through its draft
/// (<c>AdvancedTemplatesWindow.Editing.cs</c>); the files are also re-read when the window is come back to, for changes
/// made by hand or by a session.
/// </summary>
public partial class AdvancedTemplatesWindow : Window
{
    private readonly AdvancedTemplateStore _store;
    private string _shown = "";
    private bool _loading;

    /// <summary>A run of one of the project's templates: the orchestrator following it and the pane it is on.</summary>
    private sealed record BoundRun(PaneOrchestrator Orchestrator, string Pane);

    private readonly Dictionary<string, BoundRun> _runs = new(StringComparer.OrdinalIgnoreCase);
    private BoundRun? _shownRun;
    private bool _updateQueued;
    private string? _scrolledTo;

    /// <summary>The parts of a drawn step that change as a run goes on.</summary>
    private sealed record StepParts(Ellipse Node, Border Card, Border Chip, TextBlock ChipText, Button FromHere);

    private readonly Dictionary<string, StepParts> _steps = [];
    private readonly Dictionary<string, (Ellipse Node, IReadOnlyList<string> StepIds)> _stages = [];

    /// <summary>The project folder whose templates are shown.</summary>
    public string Folder { get; }

    public AdvancedTemplatesWindow(string projectFolder)
    {
        InitializeComponent();
        Controls.DarkTitleBar.Apply(this);
        Controls.WindowPlacement.KeepOnScreen(this);
        Folder = projectFolder;
        _store = new AdvancedTemplateStore(projectFolder);
        HeadingText.Text = "🗺 Advanced templates · " + Path.GetFileName(projectFolder.TrimEnd('\\', '/'));
        SubheadingText.Text = "The sequence an orchestrator follows, step by step. Kept with the project in " + _store.Folder;
        Loaded += (_, _) => Reload(_selectOnLoad, force: true);
        Activated += (_, _) => Reload(SelectedId, force: false);
        Closed += (_, _) =>
        {
            foreach (var run in _runs.Values) run.Orchestrator.Changed -= OnRunChanged;
            _runs.Clear();
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            // Editing, Esc first closes the step being edited; nothing is lost either way, every change is in the draft.
            if (_selectedStep is not null) SelectStep(null);
            else Close();
            e.Handled = true;
        };
        Closing += OnClosing;
    }

    private string? _selectOnLoad;
    private string? SelectedId => (TemplateList.SelectedItem as ListItem)?.File.Id;

    /// <summary>One line of the list on the left.</summary>
    private sealed record ListItem(AdvancedTemplateFile File, string? RunOn)
    {
        public string Title => File.Template?.Name is { Length: > 0 } name ? name : File.Id;
        public string Subtitle => (File.Template is { } t ? AdvancedTemplateTree.Summary(t) + (t.Validate().Count > 0 ? " · has problems" : "") : "Cannot be read")
                                  + (RunOn is null ? "" : $" · followed on {RunOn}");
        public Brush SubtitleBrush => File.Template is { } t && t.Validate().Count == 0 ? RunOn is null ? Muted : Purple : Amber;
        public override string ToString() => Title; // what a screen reader and UI Automation call the item
    }

    // ---- Runs ----

    /// <summary>Shows the template with that id (when the folder has it).</summary>
    public void Select(string templateId)
    {
        if (!IsLoaded) { _selectOnLoad = templateId; return; }
        Reload(templateId, force: true);
    }

    /// <summary>An orchestrator on a pane follows this template: its tree shows the run from now on, and drives it.</summary>
    public void BindRun(string templateId, PaneOrchestrator orchestrator, string pane)
    {
        if (orchestrator.Run is null) return;
        if (_runs.TryGetValue(templateId, out var bound))
        {
            if (ReferenceEquals(bound.Orchestrator, orchestrator)) return;
            bound.Orchestrator.Changed -= OnRunChanged;
        }
        _runs[templateId] = new BoundRun(orchestrator, pane);
        orchestrator.Changed += OnRunChanged;
        if (IsLoaded) Reload(SelectedId, force: true);
    }

    /// <summary>The orchestrator was taken off its pane: its run is gone and the tree is a plain template again.</summary>
    public void UnbindRun(PaneOrchestrator orchestrator)
    {
        foreach (var id in _runs.Where(r => ReferenceEquals(r.Value.Orchestrator, orchestrator)).Select(r => r.Key).ToList()) _runs.Remove(id);
        orchestrator.Changed -= OnRunChanged;
        if (IsLoaded) Reload(SelectedId, force: true);
    }

    // The orchestrator says so from any thread, and often: one redraw per burst.
    private void OnRunChanged()
    {
        if (_updateQueued) return;
        _updateQueued = true;
        _ = Dispatcher.BeginInvoke(() =>
        {
            _updateQueued = false;
            UpdateRun();
        });
    }

    private void Go_Click(object sender, RoutedEventArgs e) => _ = GoAsync(null);

    private void Pause_Click(object sender, RoutedEventArgs e) => _shownRun?.Orchestrator.PauseRun();

    private void FromHere_Click(RunStep step)
    {
        if (_shownRun?.Orchestrator.Run is not { } run) return;
        var skipped = run.Steps.TakeWhile(s => s.Step.Id != step.Step.Id).Count(s => run.StateOf(s.Step.Id) != StepState.Done);
        var text = $"Go on from \"{step.Step.DisplayTitle}\" in {step.Stage.DisplayTitle}?\n\n" +
                   (skipped == 0 ? "Every step before it is done." : skipped == 1 ? "The 1 step before it that is not done is skipped." : $"The {skipped} steps before it that are not done are skipped.") +
                   " It and every step after it are to do, also those already done.";
        if (MessageBox.Show(this, text, "Go on from this step", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        _ = GoAsync(step.Step.Id);
    }

    private async Task GoAsync(string? fromStepId)
    {
        if (_shownRun is not { } bound) return;
        try { await bound.Orchestrator.GoAsync(fromStepId); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { StatusText.Text = "The run could not go on: " + ex.Message; }
    }

    /// <summary>Brings the bar and every step up to where the shown run stands.</summary>
    private void UpdateRun()
    {
        if (_shownRun?.Orchestrator.Run is not { } run)
        {
            RunBar.Visibility = Visibility.Collapsed;
            return;
        }
        var state = run.State;
        var paused = state == RunState.Paused;
        RunBar.Visibility = Visibility.Visible;
        RunStateText.Text = OrchestratorBoxView.RunWord(run);
        RunWhere.Text = $"{_shownRun.Pane} · {run.Progress}";
        RunReason.Text = paused ? run.PauseReason ?? "" : "";
        RunReason.Visibility = RunReason.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        GoBtn.Visibility = state == RunState.Running ? Visibility.Collapsed : Visibility.Visible;
        GoBtn.Content = state == RunState.Finished ? "↺ Run again" : "▶ Go";
        GoBtn.ToolTip = state switch
        {
            RunState.Ready => "Start the run at its first step. To start further down, use ▶ from here on a step.",
            RunState.Finished => "Start the run over from its first step.",
            _ => "Carry the run on at the step it stopped at: the orchestrator looks at the worker's latest reply and takes it from there.",
        };
        PauseBtn.Visibility = state == RunState.Running ? Visibility.Visible : Visibility.Collapsed;
        UpdateApply(run);

        var editing = _draft is not null;
        var current = run.Current?.Step.Id;
        var here = paused ? Amber : Cyan;
        foreach (var (id, parts) in _steps)
        {
            var step = run.StateOf(id);
            var (fill, stroke, border, chip, chipBrush, opacity) = step switch
            {
                StepState.Done => (Green, Green, StepCardBorder, "✓ DONE", Green, 0.8),
                StepState.Skipped => (PanelFill, Faint, StepCardBorder, "SKIPPED", Faint, 0.55),
                StepState.Running => (here, here, here, !paused ? "● NOW" : run.Cause switch
                {
                    PauseCause.Owner => "⏸ PAUSED HERE",
                    PauseCause.Problem => "■ STOPPED HERE",
                    _ => "🙋 WAITING FOR YOU",
                }, here, 1.0),
                _ => (PanelFill, Muted, StepCardBorder, "", Faint, 1.0),
            };
            parts.Node.Fill = fill;
            parts.Node.Stroke = stroke;
            parts.Card.BorderBrush = border;
            parts.Card.Opacity = opacity;
            parts.ChipText.Text = chip;
            parts.ChipText.Foreground = chipBrush;
            parts.Chip.Visibility = chip.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            parts.FromHere.Visibility = editing || parts.FromHere.Tag is not RunStep ? Visibility.Collapsed : Visibility.Visible;
        }
        foreach (var (node, ids) in _stages.Values)
        {
            var brush = ids.All(id => run.StateOf(id) is StepState.Done or StepState.Skipped) && ids.Any(id => run.StateOf(id) == StepState.Done) ? Green
                : current is not null && ids.Contains(current) ? here : StageDot;
            node.Fill = brush;
            node.Stroke = brush;
        }

        // Follow the run down the tree: once per step, so the owner can scroll away from it. Not while editing.
        if (!editing && current is not null && current != _scrolledTo && _steps.TryGetValue(current, out var at))
        {
            _scrolledTo = current;
            at.Card.BringIntoView(new Rect(0, -60, at.Card.ActualWidth, at.Card.ActualHeight + 160));
        }
    }

    // ---- The folder ----

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Refresh_Click(object sender, RoutedEventArgs e) => Reload(SelectedId, force: true);

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var folder = Directory.Exists(_store.Folder) ? _store.Folder : Folder;
        try { Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { folder }, UseShellExecute = false }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { StatusText.Text = "Could not open " + folder + ": " + ex.Message; }
    }

    private async void AddExample_Click(object sender, RoutedEventArgs e)
    {
        AddExampleBtn.IsEnabled = false;
        try
        {
            var example = AdvancedTemplate.StagedBuildExample();
            await _store.SaveAsync(example);
            Reload(example.Id, force: true);
            StatusText.Text = "Wrote " + _store.PathFor(example.Id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { StatusText.Text = "Could not write the example: " + ex.Message; }
        finally { AddExampleBtn.IsEnabled = true; }
    }

    private void TemplateList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (_draft is not null && SelectedId != _draftOf) _ = StopEditingAsync(); // the draft is kept on disk
        Show((TemplateList.SelectedItem as ListItem)?.File);
    }

    /// <summary>Reads the folder again. Unless forced, nothing is redrawn when no file changed, so coming back to the window keeps its place.</summary>
    private void Reload(string? selectId, bool force)
    {
        IReadOnlyList<AdvancedTemplateFile> files;
        string stamp;
        try
        {
            files = _store.List();
            stamp = string.Join("|", files.Select(f => $"{f.Path}:{File.GetLastWriteTimeUtc(f.Path).Ticks}:{f.HasDraft}"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = "Could not read " + _store.Folder + ": " + ex.Message;
            return;
        }
        if (!force && stamp == _shown) return;
        _shown = stamp;

        var items = files.Select(f => new ListItem(f, _runs.TryGetValue(f.Id, out var run) ? run.Pane : null)).ToList();
        _loading = true;
        TemplateList.ItemsSource = items;
        TemplateList.SelectedItem = items.FirstOrDefault(i => i.File.Id == selectId) ?? items.FirstOrDefault();
        _loading = false;
        Show((TemplateList.SelectedItem as ListItem)?.File);
        StatusText.Text = files.Count == 0 ? "" : $"{(files.Count == 1 ? "1 template" : files.Count + " templates")} · read {DateTime.Now:HH:mm:ss}";
    }

    private void Show(AdvancedTemplateFile? file)
    {
        var editing = file is not null && _draft is not null && _draftOf == file.Id;
        var keepScroll = editing && _shownFile?.Id == file!.Id; // a redraw while editing keeps the place
        var offset = Scroller.VerticalOffset;
        RowsPanel.Children.Clear();
        _steps.Clear();
        _stages.Clear();
        _scrolledTo = null;
        _shownFile = file;
        _shownRun = file is not null && _runs.TryGetValue(file.Id, out var bound) ? bound : null;
        ProblemsBox.Visibility = Visibility.Collapsed;
        NoteText.Visibility = Visibility.Collapsed;
        RunBar.Visibility = Visibility.Collapsed;
        EditBar.Visibility = Visibility.Collapsed;
        EditBtn.Visibility = Visibility.Collapsed;
        if (file is null)
        {
            TreePanel.Visibility = Visibility.Collapsed;
            EmptyPanel.Visibility = Visibility.Visible;
            EmptyText.Text = "This project has no advanced templates yet.\n\nAn advanced template is a file in " + _store.Folder +
                             " that lists the stages and steps an orchestrator follows one by one.";
            return;
        }
        EmptyPanel.Visibility = Visibility.Collapsed;
        TreePanel.Visibility = Visibility.Visible;

        // A run follows the template as it was when the orchestrator was attached (or last given a change), whatever the
        // file says now. Editing shows the draft instead.
        var run = _shownRun?.Orchestrator.Run;
        if ((editing ? _draft : run?.Template ?? file.Template) is not { } template)
        {
            TemplateName.Text = file.Id;
            TemplateSummary.Text = "";
            TemplateDescription.Text = file.Path;
            ShowProblems("This file cannot be read", [file.Error ?? "Unknown error."]);
            return;
        }

        TemplateName.Text = template.Name.Length > 0 ? template.Name : file.Id;
        TemplateSummary.Text = AdvancedTemplateTree.Summary(template);
        TemplateDescription.Text = template.Description ?? "";
        TemplateDescription.Visibility = editing || string.IsNullOrWhiteSpace(template.Description) ? Visibility.Collapsed : Visibility.Visible;
        if (file.Template is not null && !editing)
        {
            EditBtn.Visibility = Visibility.Visible;
            EditBtn.Content = file.HasDraft ? "✏ Open the draft" : "✏ Edit";
        }

        IReadOnlyList<string> problems;
        if (editing)
        {
            ShowEditBar(template);
            problems = ShowDraftProblems();
        }
        else
        {
            problems = template.Validate();
            if (problems.Count > 0) ShowProblems(problems.Count == 1 ? "1 problem stops this template being run" : $"{problems.Count} problems stop this template being run", problems);
        }

        var notes = new List<string>();
        if (problems.Count == 0 && template.HasParallelStages)
            notes.Add(editing ? "Some stages do not depend on each other. Shown in the order of the file." : "Some stages do not depend on each other and could run side by side. This is the order a single worker follows.");
        if (!editing && file.HasDraft) notes.Add($"A change is waiting as a draft ({Path.GetFileName(_store.DraftPathFor(file.Id))}): ✏ Open the draft to see it, then save or discard it.");
        if (!editing && run is not null && file.Template is { } onDisk && onDisk.Serialize() != template.Serialize())
            notes.Add("The file has changed since this run was given its template. The run follows the version shown here until you ⤵ Apply the saved one to it.");
        if (editing && run is not null) notes.AddRange(RunNotes(run, template));
        if (notes.Count > 0)
        {
            NoteText.Text = string.Join("\n", notes);
            NoteText.Visibility = Visibility.Visible;
        }

        var steps = run?.Steps.ToDictionary(s => s.Step.Id);
        string? stage = null;
        var stageSteps = new List<string>();
        foreach (var row in AdvancedTemplateTree.Rows(template, fileOrder: editing))
        {
            var built = BuildRow(row, row.Kind == TreeRowKind.Step && steps is not null && row.Id is not null && steps.TryGetValue(row.Id, out var s) ? s : null,
                editing ? template : null);
            if (editing && row.Kind == TreeRowKind.End) RowsPanel.Children.Add(AddStageRow(template));
            RowsPanel.Children.Add(built.Row);
            if (row.Kind == TreeRowKind.Stage && row.Id is not null)
            {
                stage = row.Id;
                stageSteps = [];
                _stages[stage] = (built.Node, stageSteps);
            }
            else if (row.Kind == TreeRowKind.Step && row.Id is not null && built.Parts is not null)
            {
                _steps.TryAdd(row.Id, built.Parts);
                if (stage is not null) stageSteps.Add(row.Id);
            }
        }
        if (keepScroll) _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () => Scroller.ScrollToVerticalOffset(offset));
        else Scroller.ScrollToTop();
        UpdateRun();
        if (editing) FocusEditorIfAsked();
    }

    private void ShowProblems(string title, IReadOnlyList<string> problems)
    {
        ProblemsTitle.Text = "⚠ " + title;
        ProblemsText.Text = problems.Count == 1 ? problems[0] : string.Join("\n", problems.Select(p => "• " + p));
        ProblemsBox.Visibility = Visibility.Visible;
    }

    // ---- The tree ----

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private static readonly Brush Line = Frozen(0x2A, 0x3B, 0x55);
    private static readonly Brush Muted = Frozen(0x7E, 0x8C, 0xA6);
    private static readonly Brush Faint = Frozen(0x5A, 0x6B, 0x85);
    private static readonly Brush Amber = Frozen(0xFB, 0xBF, 0x24);
    private static readonly Brush Cyan = Frozen(0x2F, 0xD4, 0xF5);
    private static readonly Brush Green = Frozen(0x34, 0xE5, 0xA8);
    private static readonly Brush Purple = Frozen(0xC4, 0xB5, 0xFD);
    private static readonly Brush StageDot = Frozen(0xA7, 0x8B, 0xFA); // the orchestrator's purple
    private static readonly Brush StepCard = Frozen(0x0F, 0x17, 0x24);
    private static readonly Brush StepCardBorder = Frozen(0x1B, 0x27, 0x38);
    private static readonly Brush PanelFill = Frozen(0x0C, 0x12, 0x1C);
    private static readonly Brush Text = Frozen(0xE8, 0xF1, 0xFB);
    private static readonly Brush ButtonFill = Frozen(0x16, 0x20, 0x2F);
    private static readonly Brush ButtonText = Frozen(0xB6, 0xC2, 0xD6);

    private const double GutterWidth = 30;

    /// <summary>One row: the trunk with this row's node on the left, what the row says on the right.</summary>
    /// <param name="step">The step of the shown run this row stands for; null without a run, and for rows that are not steps.</param>
    /// <param name="draft">The draft being edited, whose stages and steps the rows get their editing controls for; null when not editing.</param>
    private (Grid Row, Ellipse Node, StepParts? Parts) BuildRow(TreeRow row, RunStep? step, AdvancedTemplate? draft = null)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(GutterWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition());

        // The trunk runs through every row; it starts at the Start node and stops at the Done node.
        var trunk = new Rectangle { Width = 2, Fill = Line, HorizontalAlignment = HorizontalAlignment.Center, SnapsToDevicePixels = true };
        Grid.SetRow(trunk, row.Kind == TreeRowKind.Start ? 1 : 0);
        Grid.SetRowSpan(trunk, row.Kind is TreeRowKind.Start or TreeRowKind.End ? 1 : 2);
        grid.Children.Add(trunk);

        Ellipse node;
        FrameworkElement content;
        StepParts? parts = null;
        switch (row.Kind)
        {
            case TreeRowKind.Stage:
                node = Node(14, StageDot, StageDot, top: 21);
                content = draft?.Stages.FirstOrDefault(s => s.Id == row.Id) is { } editedStage ? EditStageContent(row, draft, editedStage) : StageContent(row);
                break;
            case TreeRowKind.Step:
                node = Node(10, PanelFill, Muted, top: 17);
                parts = StepContent(row, node, step, draft?.AllSteps.FirstOrDefault(s => s.Id == row.Id) is { } editedStep ? (draft, editedStep) : null);
                content = parts.Card;
                break;
            default:
                node = Node(12, row.Kind == TreeRowKind.Start ? Muted : PanelFill, Muted, top: null);
                content = CapContent(row);
                break;
        }
        Grid.SetRowSpan(node, 2);
        grid.Children.Add(node);
        Grid.SetColumn(content, 1);
        Grid.SetRowSpan(content, 2);
        grid.Children.Add(content);
        return (grid, node, parts);
    }

    /// <summary>A node on the trunk: centred in a one-line row, else level with the row's first line.</summary>
    private static Ellipse Node(double size, Brush fill, Brush stroke, double? top) => new()
    {
        Width = size,
        Height = size,
        Fill = fill,
        Stroke = stroke,
        StrokeThickness = 2,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = top is null ? VerticalAlignment.Center : VerticalAlignment.Top,
        Margin = new Thickness(0, top is { } t ? t - size / 2 : 0, 0, 0),
    };

    private static TextBlock CapContent(TreeRow row) => new()
    {
        Text = row.Title.ToUpperInvariant(),
        Foreground = Faint,
        FontSize = 10,
        FontWeight = FontWeights.Bold,
        Margin = new Thickness(8, 6, 0, 6),
        VerticalAlignment = VerticalAlignment.Center,
    };

    private static StackPanel StageContent(TreeRow row)
    {
        var panel = new StackPanel { Margin = new Thickness(8, 10, 0, 4) };
        panel.Children.Add(new TextBlock { Text = row.Title, Foreground = Text, FontSize = 14, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        if (row.Detail is { } detail) panel.Children.Add(new TextBlock { Text = detail, Foreground = Muted, FontSize = 11, Margin = new Thickness(0, 1, 0, 0), TextWrapping = TextWrapping.Wrap });
        return panel;
    }

    private static Border Pill(TextBlock text, Brush background, Brush border) => new()
    {
        Background = background,
        BorderBrush = border,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(7, 1, 7, 2),
        Margin = new Thickness(10, 0, 0, 0),
        VerticalAlignment = VerticalAlignment.Center,
        Child = text,
    };

    /// <param name="edit">The draft and its step this row stands for, when editing: the row gets its controls, and its editor when picked.</param>
    private StepParts StepContent(TreeRow row, Ellipse node, RunStep? step, (AdvancedTemplate Draft, TemplateStep Step)? edit = null)
    {
        var head = new DockPanel { LastChildFill = true };
        if (edit is { } e) AddStepTools(head, e.Draft, e.Step);
        // Right to left: the kind, the escalation flag, how far a run has got with the step, and its "from here" button.
        var kind = new TextBlock { Text = (row.Label ?? "").ToUpperInvariant(), Foreground = Faint, FontSize = 9.5, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 1, 0, 0) };
        DockPanel.SetDock(kind, Dock.Right);
        head.Children.Add(kind);
        if (row.Escalate)
        {
            var flag = Pill(new TextBlock { Text = "⚑ pauses for you", Foreground = Amber, FontSize = 10, FontWeight = FontWeights.SemiBold }, Frozen(0x2B, 0x21, 0x10), Frozen(0x7A, 0x5A, 0x12));
            flag.ToolTip = "An escalation point: anything here the orchestrator cannot answer from your instructions pauses the run and messages you.";
            DockPanel.SetDock(flag, Dock.Right);
            head.Children.Add(flag);
        }
        var chipText = new TextBlock { FontSize = 10, FontWeight = FontWeights.Bold };
        var chip = Pill(chipText, PanelFill, StepCardBorder);
        chip.Visibility = Visibility.Collapsed;
        DockPanel.SetDock(chip, Dock.Right);
        head.Children.Add(chip);

        var fromHere = new Button
        {
            Content = "▶ from here",
            Background = ButtonFill,
            Foreground = ButtonText,
            Padding = new Thickness(7, 1, 7, 2),
            Margin = new Thickness(10, 0, 0, 0),
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed, // shown once a run is bound
            Opacity = 0,                       // and then only under the pointer or with the keyboard on it
            ToolTip = "Send the run on from this step, whatever it is doing now. Steps before it that are not done are skipped.",
        };
        System.Windows.Automation.AutomationProperties.SetName(fromHere, "Go on from: " + row.Title);
        if (step is not null)
        {
            fromHere.Tag = step;
            fromHere.Click += (_, _) => FromHere_Click(step);
        }
        DockPanel.SetDock(fromHere, Dock.Right);
        head.Children.Add(fromHere);

        var icon = new TextBlock { Text = row.Icon, FontFamily = new System.Windows.Media.FontFamily("Segoe UI Emoji"), FontSize = 12, Width = 22, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 0, 0) };
        DockPanel.SetDock(icon, Dock.Left);
        head.Children.Add(icon);
        var title = new TextBlock { Text = row.Title, Foreground = Text, FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
        head.Children.Add(title);

        var body = new StackPanel();
        body.Children.Add(head);
        var detailText = new TextBlock { Text = row.Detail ?? "", Foreground = Muted, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(22, 3, 0, 0) };
        detailText.Visibility = row.Detail is null ? Visibility.Collapsed : Visibility.Visible;
        body.Children.Add(detailText);
        if (edit is { } picked)
        {
            head.Cursor = Cursors.Hand;
            head.Background = Brushes.Transparent; // so the whole head line takes the click, not only its text
            head.MouseLeftButtonUp += (_, _) => SelectStep(ReferenceEquals(_selectedStep, picked.Step) ? null : picked.Step);
            if (ReferenceEquals(_selectedStep, picked.Step)) body.Children.Add(StepEditor(picked.Draft, picked.Step, title, detailText));
        }

        var card = new Border
        {
            Background = StepCard,
            BorderBrush = StepCardBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 7, 10, 8),
            Margin = new Thickness(8, 3, 0, 3),
            Child = body,
        };
        void Reveal() => fromHere.Opacity = card.IsMouseOver || fromHere.IsKeyboardFocused ? 1 : 0;
        card.MouseEnter += (_, _) => Reveal();
        card.MouseLeave += (_, _) => Reveal();
        fromHere.GotKeyboardFocus += (_, _) => Reveal();
        fromHere.LostKeyboardFocus += (_, _) => Reveal();
        return new StepParts(node, card, chip, chipText, fromHere);
    }
}
