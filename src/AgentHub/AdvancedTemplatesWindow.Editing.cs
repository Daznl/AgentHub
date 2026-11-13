using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shapes;
using System.Windows.Threading;
using AgentHub.Services.Orchestrator;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using MessageBox = System.Windows.MessageBox;
using Orientation = System.Windows.Controls.Orientation;
using Rectangle = System.Windows.Shapes.Rectangle;
using TextBox = System.Windows.Controls.TextBox;

namespace AgentHub;

// ✏ Editing a template. Every change goes to the draft beside the file (orchestration/<id>.draft.json), written a moment
// after the last keystroke; 💾 Save puts the draft in the template's place (AdvancedTemplateStore.AcceptDraftAsync), which
// refuses a draft with problems; 🗑 Discard drops it. A run on a pane keeps the version it was given until ⤵ Apply to this
// run, and only while it stands still; the steps it has reached are locked here, as ProblemsWith would refuse changing them.
public partial class AdvancedTemplatesWindow
{
    private AdvancedTemplate? _draft;
    private string? _draftOf;
    private TemplateStep? _selectedStep;
    private bool _draftDirty;
    private DateTime? _draftSavedAt;
    private AdvancedTemplateFile? _shownFile;
    private DispatcherTimer? _draftTimer;
    private readonly SemaphoreSlim _draftGate = new(1, 1);
    private IReadOnlySet<string> _locked = new HashSet<string>();
    private bool _settingFields;
    private bool _focusEditor;
    private TextBox? _editorFocus;
    private bool _closeAfterWrite;

    // ---- Starting and stopping ----

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (_shownFile is not { Template: { } template } file) return;
        AdvancedTemplate draft;
        try { draft = _store.ReadDraft(file.Id) ?? template.Clone(); }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
        {
            var answer = MessageBox.Show(this, $"The draft of this template cannot be read:\n\n{ex.Message}\n\nDiscard it and start again from the saved template?",
                "The draft cannot be read", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.OK) return;
            try { _store.DiscardDraft(file.Id); }
            catch (Exception io) when (io is IOException or UnauthorizedAccessException) { StatusText.Text = "Could not remove the draft: " + io.Message; return; }
            draft = template.Clone();
        }
        _draft = draft;
        _draftOf = file.Id;
        _selectedStep = null;
        _draftDirty = false;
        _draftSavedAt = null;
        Show(file);
        StatusText.Text = "Editing. Every change is kept in the draft; the template changes when you save.";
    }

    /// <summary>Leaves editing; a change not yet written goes to the draft first. False when it could not be written.</summary>
    private async Task<bool> StopEditingAsync()
    {
        var (draft, id, dirty) = (_draft, _draftOf, _draftDirty);
        _draftTimer?.Stop();
        _draft = null;
        _draftOf = null;
        _selectedStep = null;
        _draftDirty = false;
        _locked = new HashSet<string>();
        return draft is null || id is null || !dirty || await WriteDraftAsync(id, draft);
    }

    private async void SaveDraft_Click(object sender, RoutedEventArgs e)
    {
        if (_draft is not { } draft || _draftOf is not { } id) return;
        if (draft.Validate().Count > 0)
        {
            StatusText.Text = "Not saved: the draft has problems (listed above).";
            return;
        }
        // Editing stops first, so no keystroke can write the draft again behind the save.
        if (!await StopEditingAsync()) { Resume(draft, id); return; }
        try
        {
            if (!_store.HasDraft(id))
            {
                Reload(id, force: true);
                StatusText.Text = "Nothing was changed, so nothing was saved.";
                return;
            }
            await _store.AcceptDraftAsync(id);
            Reload(id, force: true);
            var stale = _shownRun is { } bound && bound.Orchestrator.Run is { } run && _shownFile?.Template is { } saved && saved.Serialize() != run.Template.Serialize();
            StatusText.Text = $"Saved {_store.PathFor(id)} · {DateTime.Now:HH:mm:ss}" +
                              (stale ? $". The run on {_shownRun!.Pane} still follows the version it was given: ⤵ Apply to this run gives it this one." : "");
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or IOException or UnauthorizedAccessException)
        {
            Resume(draft, id);
            StatusText.Text = "Not saved: " + ex.Message;
        }
    }

    /// <summary>Back to editing <paramref name="draft"/>, after a save that did not go through.</summary>
    private void Resume(AdvancedTemplate draft, string id)
    {
        _draft = draft;
        _draftOf = id;
        Reload(id, force: true);
    }

    private async void DiscardDraft_Click(object sender, RoutedEventArgs e)
    {
        if (_draftOf is not { } id) return;
        if ((_store.HasDraft(id) || _draftDirty) &&
            MessageBox.Show(this, "Discard this draft? Every change in it is lost; the template stays as it was saved.", "Discard the draft",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        _draftDirty = false;
        await StopEditingAsync();
        await _draftGate.WaitAsync();
        try
        {
            if (_store.HasDraft(id)) _store.DiscardDraft(id);
            StatusText.Text = "The draft was discarded.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { StatusText.Text = "Could not remove the draft: " + ex.Message; }
        finally { _draftGate.Release(); }
        Reload(id, force: true);
    }

    private async void DoneEditing_Click(object sender, RoutedEventArgs e)
    {
        if (_draftOf is not { } id) return;
        var written = await StopEditingAsync();
        Reload(id, force: true);
        if (written) StatusText.Text = _store.HasDraft(id) ? "The draft is kept beside the template, not saved: ✏ Open the draft to go on with it." : "Nothing was changed.";
    }

    /// <summary>Closing while a change is not yet written: it is written first.</summary>
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closeAfterWrite || _draft is null || !_draftDirty) return;
        e.Cancel = true;
        _ = CloseAfterWriteAsync();
    }

    private async Task CloseAfterWriteAsync()
    {
        if (!await StopEditingAsync() &&
            MessageBox.Show(this, "The last changes could not be written to the draft (see the status line). Close anyway and lose them?", "Close",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        _closeAfterWrite = true;
        Close();
    }

    // ---- Writing the draft ----

    /// <summary>The draft changed: it is written a moment after the last change. <paramref name="redraw"/> for changes the tree must show at once.</summary>
    private void DraftChanged(bool redraw)
    {
        if (_draft is not { } draft) return;
        _draftDirty = true;
        _draftTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(600), DispatcherPriority.Background, (_, _) => _ = FlushDraftAsync(), Dispatcher);
        _draftTimer.Stop();
        _draftTimer.Start();
        if (redraw) Show(_shownFile);
        else
        {
            TemplateSummary.Text = AdvancedTemplateTree.Summary(draft);
            ShowDraftProblems();
        }
    }

    private async Task FlushDraftAsync()
    {
        _draftTimer?.Stop();
        if (_draft is not { } draft || _draftOf is not { } id || !_draftDirty) return;
        _draftDirty = false; // a change made while writing sets it again
        if (!await WriteDraftAsync(id, draft) && ReferenceEquals(_draft, draft)) _draftDirty = true;
        if (ReferenceEquals(_draft, draft)) UpdateEditState();
    }

    private async Task<bool> WriteDraftAsync(string id, AdvancedTemplate draft)
    {
        await _draftGate.WaitAsync();
        try
        {
            await _store.SaveDraftAsync(id, draft);
            _draftSavedAt = DateTime.Now;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = "The draft could not be written: " + ex.Message;
            return false;
        }
        finally { _draftGate.Release(); }
    }

    // ---- The bar, problems and notes ----

    private void ShowEditBar(AdvancedTemplate draft)
    {
        EditBar.Visibility = Visibility.Visible;
        _editorFocus = null;
        var run = _shownRun?.Orchestrator.Run;
        _locked = run is { State: not RunState.Ready } ? run.StartedStepIds.ToHashSet(StringComparer.Ordinal) : new HashSet<string>();
        _settingFields = true;
        if (DraftNameBox.Text != draft.Name) DraftNameBox.Text = draft.Name;
        if (DraftDescriptionBox.Text != (draft.Description ?? "")) DraftDescriptionBox.Text = draft.Description ?? "";
        _settingFields = false;
        UpdateEditState();
    }

    private void UpdateEditState()
    {
        if (_draft is not { } draft || _draftOf is not { } id) return;
        var problems = draft.Validate().Count;
        var where = _draftDirty ? "Draft · writing…" : _draftSavedAt is { } at ? $"Draft written {at:HH:mm:ss}" : _store.HasDraft(id) ? "Draft" : "No change yet";
        EditStateText.Text = where + (problems == 0 ? " · ready to save" : problems == 1 ? " · 1 problem to fix before saving" : $" · {problems} problems to fix before saving");
        SaveDraftBtn.IsEnabled = problems == 0;
        SaveDraftBtn.ToolTip = problems == 0
            ? "Put the draft in the template's place. A run on a pane keeps the version it has until you ⤵ Apply this one to it."
            : "The draft has problems (listed above): fix them first.";
        ToolTipService.SetShowOnDisabled(SaveDraftBtn, true);
    }

    /// <summary>The draft's problems in the amber box; what it returns stops a save.</summary>
    private IReadOnlyList<string> ShowDraftProblems()
    {
        if (_draft is not { } draft) return [];
        var problems = draft.Validate();
        if (problems.Count > 0) ShowProblems(problems.Count == 1 ? "1 problem to fix before this draft can be saved" : $"{problems.Count} problems to fix before this draft can be saved", problems);
        else ProblemsBox.Visibility = Visibility.Collapsed;
        UpdateEditState();
        return problems;
    }

    /// <summary>What editing means for the run on a pane: the locked steps, and whether this draft could be given to it.</summary>
    private IEnumerable<string> RunNotes(TemplateRun run, AdvancedTemplate draft)
    {
        var pane = _shownRun?.Pane ?? "a pane";
        if (run.State == RunState.Ready)
        {
            yield return $"The run on {pane} has not started: once saved, ⤵ Apply to this run gives it this version.";
            yield break;
        }
        if (draft.Validate().Count > 0) yield break;
        var problems = run.ProblemsWith(draft);
        var reached = run.StartedStepIds.Count;
        yield return problems.Count == 0
            ? $"🔒 The run on {pane} has reached {(reached == 1 ? "1 step" : reached + " steps")}: they stay as they are. Once saved, ⤵ Apply to this run gives it this version" +
              (run.State == RunState.Running ? " (pause it first)." : ".")
            : $"⚠ The run on {pane} could not be given this version: " + string.Join(" ", problems);
    }

    private void DraftNameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_settingFields || _draft is not { } draft) return;
        draft.Name = DraftNameBox.Text;
        TemplateName.Text = draft.Name.Trim().Length > 0 ? draft.Name : _draftOf ?? "";
        DraftChanged(redraw: false);
    }

    private void DraftDescriptionBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_settingFields || _draft is not { } draft) return;
        draft.Description = string.IsNullOrWhiteSpace(DraftDescriptionBox.Text) ? null : DraftDescriptionBox.Text;
        DraftChanged(redraw: false);
    }

    // ---- Apply to this run ----

    /// <summary>⤵ Apply shows when the saved template is not the one the run follows.</summary>
    private void UpdateApply(TemplateRun run)
    {
        var differs = _draft is null && _shownFile?.Template is { } saved && saved.Serialize() != run.Template.Serialize();
        ApplyBtn.Visibility = differs ? Visibility.Visible : Visibility.Collapsed;
        ApplyBtn.IsEnabled = run.State != RunState.Running;
        ApplyBtn.ToolTip = run.State == RunState.Running
            ? "Pause the run first: its template can only be changed while it stands still."
            : "Have the run follow the template as it is saved now. Steps it has done keep their ticks; new steps are to do.";
        ToolTipService.SetShowOnDisabled(ApplyBtn, true);
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (_shownRun is not { } bound || bound.Orchestrator.Run is not { } run || _shownFile is not { } file) return;
        AdvancedTemplate saved;
        try { saved = _store.Read(file.Id); }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException) { StatusText.Text = "The saved template cannot be read: " + ex.Message; return; }

        var problems = run.ProblemsWith(saved);
        if (problems.Count > 0)
        {
            MessageBox.Show(this, "The saved template cannot be given to this run:\n\n" + string.Join("\n", problems.Select(p => "• " + p)) +
                "\n\nThe steps a run has reached stay as they are. Change the template back for those, or carry on with the run as it is.",
                "Apply to this run", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var ids = run.Steps.Select(s => s.Step.Id).ToHashSet(StringComparer.Ordinal);
        var savedIds = saved.AllSteps.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        var added = savedIds.Count(id => !ids.Contains(id));
        var removed = ids.Count(id => !savedIds.Contains(id));
        var changed = saved.AllSteps.Count(s => ids.Contains(s.Id) && !run.Steps.First(r => r.Step.Id == s.Id).Step.SameAs(s));
        string Count(int n, string what) => n == 1 ? $"1 step {what}" : $"{n} steps {what}";
        var text = $"Have the run on {bound.Pane} follow the template as it is saved now?\n\n" +
                   string.Join(", ", new[] { Count(added, "added"), Count(removed, "removed"), Count(changed, "changed") }.Where(s => !s.StartsWith("0 "))) +
                   ". Steps the run has done keep their ticks; the orchestrator is shown the new outline with its next message.";
        if (MessageBox.Show(this, text, "Apply to this run", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        try
        {
            bound.Orchestrator.ApplyTemplate(saved);
            Reload(file.Id, force: true);
            StatusText.Text = $"The run on {bound.Pane} follows the saved template now · {bound.Orchestrator.Run?.Progress}";
        }
        catch (InvalidOperationException ex) { StatusText.Text = "Not applied: " + ex.Message; }
    }

    // ---- The tree's editing controls ----

    /// <summary>Ids a new step or stage must not take: the saved template's and the run's, as a run records progress by them.</summary>
    private IEnumerable<string> TakenStepIds() =>
        (_shownFile?.Template?.AllSteps.Select(s => s.Id) ?? []).Concat(_shownRun?.Orchestrator.Run?.Steps.Select(s => s.Step.Id) ?? []);

    private IEnumerable<string> TakenStageIds() =>
        (_shownFile?.Template?.Stages.Select(s => s.Id) ?? []).Concat(_shownRun?.Orchestrator.Run?.Template.Stages.Select(s => s.Id) ?? []);

    private void SelectStep(TemplateStep? step)
    {
        _selectedStep = step;
        _focusEditor = step is not null;
        Show(_shownFile);
    }

    private void FocusEditorIfAsked()
    {
        if (!_focusEditor || _editorFocus is not { } box) return;
        _focusEditor = false;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            box.Focus();
            Keyboard.Focus(box);
            box.CaretIndex = box.Text.Length;
            box.BringIntoView();
        });
    }

    private Button Tool(string text, string name, bool enabled, Action click)
    {
        var button = new Button
        {
            Content = text,
            Background = ButtonFill,
            Foreground = ButtonText,
            Padding = new Thickness(6, 0, 6, 1),
            Margin = new Thickness(4, 0, 0, 0),
            FontSize = 11,
            MinWidth = 24,
            VerticalAlignment = VerticalAlignment.Center,
            IsEnabled = enabled,
            ToolTip = name,
        };
        AutomationProperties.SetName(button, name);
        button.Click += (_, _) => click();
        return button;
    }

    private static TextBlock LockMark(string why) => new()
    {
        Text = "🔒",
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI Emoji"),
        FontSize = 11,
        Margin = new Thickness(10, 0, 0, 0),
        VerticalAlignment = VerticalAlignment.Center,
        ToolTip = why,
    };

    /// <summary>A step's ↑ ↓ ✏ ✕, on the right of its head line; a lock instead when the run has reached it.</summary>
    private void AddStepTools(DockPanel head, AdvancedTemplate draft, TemplateStep step)
    {
        FrameworkElement tools;
        if (_locked.Contains(step.Id)) tools = LockMark("The run has reached this step, so it stays as it is.");
        else
        {
            var order = draft.Stages.SelectMany(s => s.Steps).ToList();
            var index = order.IndexOf(step);
            var aboveLocked = index > 0 && _locked.Contains(order[index - 1].Id);
            var title = step.DisplayTitle;
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            panel.Children.Add(Tool("↑", "Move up: " + title, TemplateEditing.CanMoveStep(draft, step, -1) && !aboveLocked, () =>
            {
                TemplateEditing.MoveStep(draft, step, -1);
                DraftChanged(redraw: true);
            }));
            panel.Children.Add(Tool("↓", "Move down: " + title, TemplateEditing.CanMoveStep(draft, step, +1), () =>
            {
                TemplateEditing.MoveStep(draft, step, +1);
                DraftChanged(redraw: true);
            }));
            panel.Children.Add(Tool("✏", "Edit: " + title, true, () => SelectStep(ReferenceEquals(_selectedStep, step) ? null : step)));
            panel.Children.Add(Tool("✕", "Remove: " + title, true, () =>
            {
                TemplateEditing.RemoveStep(draft, step);
                if (ReferenceEquals(_selectedStep, step)) _selectedStep = null;
                DraftChanged(redraw: true);
            }));
            tools = panel;
        }
        DockPanel.SetDock(tools, Dock.Right);
        head.Children.Add(tools);
    }

    /// <summary>A stage heading while editing: its title to type over, ➕ Step, ↑ ↓ ✕.</summary>
    private FrameworkElement EditStageContent(TreeRow row, AdvancedTemplate draft, TemplateStage stage)
    {
        var locked = stage.Steps.Any(s => _locked.Contains(s.Id));
        var index = draft.Stages.IndexOf(stage);
        var aboveLocked = index > 0 && draft.Stages[index - 1].Steps.Any(s => _locked.Contains(s.Id));
        var title = stage.DisplayTitle;

        var tools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        tools.Children.Add(Tool("➕ Step", "Add a step to: " + title, true, () =>
        {
            _selectedStep = TemplateEditing.AddStep(draft, stage, -1, StepKind.Instruct, TakenStepIds());
            _focusEditor = true;
            DraftChanged(redraw: true);
        }));
        if (locked) tools.Children.Add(LockMark("The run has reached steps of this stage: it stays in its place and cannot be removed."));
        else
        {
            tools.Children.Add(Tool("↑", "Move stage up: " + title, TemplateEditing.CanMoveStage(draft, stage, -1) && !aboveLocked, () =>
            {
                TemplateEditing.MoveStage(draft, stage, -1);
                DraftChanged(redraw: true);
            }));
            tools.Children.Add(Tool("↓", "Move stage down: " + title, TemplateEditing.CanMoveStage(draft, stage, +1), () =>
            {
                TemplateEditing.MoveStage(draft, stage, +1);
                DraftChanged(redraw: true);
            }));
            tools.Children.Add(Tool("✕", "Remove stage: " + title, true, () =>
            {
                if (stage.Steps.Count > 0 && MessageBox.Show(this, $"Remove \"{title}\" and its {(stage.Steps.Count == 1 ? "step" : stage.Steps.Count + " steps")}?\n\nOnly the draft changes until you save.",
                        "Remove stage", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
                TemplateEditing.RemoveStage(draft, stage);
                if (_selectedStep is not null && stage.Steps.Contains(_selectedStep)) _selectedStep = null;
                DraftChanged(redraw: true);
            }));
        }

        var line = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(tools, Dock.Right);
        line.Children.Add(tools);
        var box = new TextBox { Text = stage.Title ?? "", FontSize = 14, FontWeight = FontWeights.SemiBold, Padding = new Thickness(4, 1, 4, 1), VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(box, "Stage title: " + title);
        box.TextChanged += (_, _) =>
        {
            stage.Title = string.IsNullOrWhiteSpace(box.Text) ? null : box.Text;
            DraftChanged(redraw: false);
        };
        line.Children.Add(box);

        var panel = new StackPanel { Margin = new Thickness(8, 8, 0, 4) };
        panel.Children.Add(line);
        if (row.Detail is { } detail) panel.Children.Add(new TextBlock { Text = detail, Foreground = Muted, FontSize = 11, Margin = new Thickness(4, 3, 0, 0), TextWrapping = TextWrapping.Wrap });
        return panel;
    }

    /// <summary>The row before Done while editing: ➕ Add a stage.</summary>
    private FrameworkElement AddStageRow(AdvancedTemplate draft)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(GutterWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.Children.Add(new Rectangle { Width = 2, Fill = Line, HorizontalAlignment = HorizontalAlignment.Center, SnapsToDevicePixels = true });
        var button = new Button
        {
            Content = "➕ Add a stage",
            Background = ButtonFill,
            Foreground = ButtonText,
            Padding = new Thickness(10, 3, 10, 4),
            Margin = new Thickness(8, 10, 0, 8),
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Left,
            ToolTip = "A new stage at the end, with a first step to fill in.",
        };
        button.Click += (_, _) =>
        {
            var stage = TemplateEditing.AddStage(draft, -1, TakenStageIds());
            _selectedStep = TemplateEditing.AddStep(draft, stage, 0, StepKind.Instruct, TakenStepIds());
            _focusEditor = true;
            DraftChanged(redraw: true);
        };
        Grid.SetColumn(button, 1);
        grid.Children.Add(button);
        return grid;
    }

    // ---- The step editor ----

    private static string KindHelp(StepKind kind) => kind switch
    {
        StepKind.Instruct => "The orchestrator sends the instruction to the worker and judges its reply.",
        StepKind.WaitForWorker => "Nothing is sent: the orchestrator watches until the worker's work is finished.",
        StepKind.AskOwner => "The run stops and puts the question to you; your answer carries it on.",
        StepKind.NewSession => "AgentHub starts a fresh worker session in the pane, the way the last one was started.",
        StepKind.CloseSession => "AgentHub ends the worker session.",
        StepKind.Handoff => "The worker writes its handoff notes and updates the project's documents.",
        StepKind.CreateBranch => "The worker creates the branch and switches to it.",
        StepKind.Commit => "The worker commits its work to the current branch.",
        StepKind.MergeBranch => "The worker merges the branch into the target, with no force.",
        StepKind.MoveWorkToBranch => "The worker moves its uncommitted work onto the branch.",
        StepKind.OpenPullRequest => "The worker pushes the branch and opens a pull request into the target.",
        _ => "",
    };

    private static (string Label, string Hint) TextField(StepKind kind) => kind switch
    {
        StepKind.Instruct => ("Instruction", "Needed: what the worker is told. It cannot see the template."),
        StepKind.AskOwner => ("Question", "Needed: what the run asks you."),
        StepKind.WaitForWorker => ("Watch for", "Optional: what the orchestrator should check before calling the work finished."),
        StepKind.NewSession or StepKind.CloseSession => ("Note", "Optional."),
        _ => ("Extra detail", "Optional: added to what the worker is told."),
    };

    private static bool UsesBranch(StepKind kind) => kind is StepKind.CreateBranch or StepKind.MoveWorkToBranch or StepKind.MergeBranch;
    private static bool UsesTarget(StepKind kind) => kind is StepKind.MergeBranch or StepKind.OpenPullRequest;

    private static TextBox Box(string? value, string name, bool multiLine) => Named(new TextBox
    {
        Text = value ?? "",
        FontSize = 12,
        Padding = new Thickness(4, 2, 4, 2),
        TextWrapping = multiLine ? TextWrapping.Wrap : TextWrapping.NoWrap,
        AcceptsReturn = multiLine,
        MinHeight = multiLine ? 54 : 0,
        MaxHeight = multiLine ? 180 : double.PositiveInfinity,
        VerticalScrollBarVisibility = multiLine ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled,
    }, name);

    private static T Named<T>(T element, string name) where T : FrameworkElement
    {
        AutomationProperties.SetName(element, name);
        return element;
    }

    /// <summary>The picked step's fields, under its head line. Typing changes the draft at once; a change of kind redraws the editor.</summary>
    private FrameworkElement StepEditor(AdvancedTemplate draft, TemplateStep step, TextBlock title, TextBlock detail)
    {
        var grid = new Grid { Margin = new Thickness(22, 10, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        void Field(string label, FrameworkElement input, string? hint = null)
        {
            var r = grid.RowDefinitions.Count;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var name = new TextBlock { Text = label, Foreground = Muted, FontSize = 11, Margin = new Thickness(0, 8, 12, 0), VerticalAlignment = VerticalAlignment.Top };
            Grid.SetRow(name, r);
            grid.Children.Add(name);
            var cell = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
            cell.Children.Add(input);
            if (hint is not null) cell.Children.Add(new TextBlock { Text = hint, Foreground = Faint, FontSize = 10.5, Margin = new Thickness(1, 2, 0, 0), TextWrapping = TextWrapping.Wrap });
            Grid.SetRow(cell, r);
            Grid.SetColumn(cell, 1);
            grid.Children.Add(cell);
        }
        void Refresh()
        {
            title.Text = step.DisplayTitle;
            var text = step.Text?.Trim();
            detail.Text = text ?? "";
            detail.Visibility = string.IsNullOrEmpty(text) || text == step.DisplayTitle ? Visibility.Collapsed : Visibility.Visible;
            DraftChanged(redraw: false);
        }

        var kind = Named(new ComboBox { FontSize = 12, MinWidth = 220, HorizontalAlignment = HorizontalAlignment.Left }, "Kind");
        foreach (var k in Enum.GetValues<StepKind>())
        {
            var item = new ComboBoxItem { Content = $"{AdvancedTemplateTree.Icon(k)}  {AdvancedTemplateTree.Label(k)}", Tag = k };
            kind.Items.Add(item);
            if (k == step.Kind) kind.SelectedItem = item;
        }
        kind.SelectionChanged += (_, _) =>
        {
            if (kind.SelectedItem is not ComboBoxItem { Tag: StepKind k } || k == step.Kind) return;
            step.Kind = k;
            if (!UsesBranch(k)) step.Branch = null;
            if (!UsesTarget(k)) step.Target = null;
            _focusEditor = false;
            DraftChanged(redraw: true);
        };
        Field("Kind", kind, KindHelp(step.Kind));

        var titleBox = Box(step.Title, "Title", multiLine: false);
        titleBox.TextChanged += (_, _) =>
        {
            step.Title = string.IsNullOrWhiteSpace(titleBox.Text) ? null : titleBox.Text;
            Refresh();
        };
        Field("Title", titleBox, "Optional: the label on the tree. Left empty, it is made from the kind and text.");

        var (label, hint) = TextField(step.Kind);
        var textBox = Box(step.Text, label, multiLine: true);
        textBox.TextChanged += (_, _) =>
        {
            step.Text = string.IsNullOrWhiteSpace(textBox.Text) ? null : textBox.Text;
            Refresh();
        };
        Field(label, textBox, hint);
        _editorFocus = step.Kind is StepKind.Instruct or StepKind.AskOwner ? textBox : titleBox;

        if (UsesBranch(step.Kind))
        {
            var branch = Box(step.Branch, "Branch", multiLine: false);
            branch.TextChanged += (_, _) =>
            {
                step.Branch = string.IsNullOrWhiteSpace(branch.Text) ? null : branch.Text.Trim();
                Refresh();
            };
            Field("Branch", branch, step.Kind == StepKind.MergeBranch ? "Needed: the branch to merge." : "Needed.");
            if (step.Kind == StepKind.CreateBranch) _editorFocus = branch;
        }
        if (UsesTarget(step.Kind))
        {
            var target = Box(step.Target, "Target", multiLine: false);
            target.TextChanged += (_, _) =>
            {
                step.Target = string.IsNullOrWhiteSpace(target.Text) ? null : target.Text.Trim();
                Refresh();
            };
            Field("Target", target, step.Kind == StepKind.MergeBranch ? "Needed: the branch merged into." : "Optional: the trunk when empty.");
        }

        var escalate = Named(new CheckBox
        {
            Content = "⚑ Escalation point: anything here your instructions do not settle pauses the run and asks you",
            IsChecked = step.Escalate,
            Foreground = Text,
            FontSize = 11.5,
            Margin = new Thickness(0, 4, 0, 0),
        }, "Escalation point");
        escalate.Click += (_, _) =>
        {
            step.Escalate = escalate.IsChecked == true;
            DraftChanged(redraw: true);
        };
        Field("", escalate);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        buttons.Children.Add(Tool("➕ Step below", "Add a step below: " + step.DisplayTitle, true, () =>
        {
            if (TemplateEditing.StageOf(draft, step) is not { } stage) return;
            _selectedStep = TemplateEditing.AddStep(draft, stage, stage.Steps.IndexOf(step) + 1, StepKind.Instruct, TakenStepIds());
            _focusEditor = true;
            DraftChanged(redraw: true);
        }));
        buttons.Children.Add(Tool("Close", "Close the editor", true, () => SelectStep(null)));
        Field("", buttons);
        return grid;
    }
}
