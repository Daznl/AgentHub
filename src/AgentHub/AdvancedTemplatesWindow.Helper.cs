using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AgentHub.Services;
using AgentHub.Services.Help;
using AgentHub.Services.Orchestrator;
using Button = System.Windows.Controls.Button;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace AgentHub;

// 🦀 The Template helper (stage 6): the crab at the top right opens it beside the tree. Given a written plan it builds a
// template; given a request it changes the one on screen. It reads the project but writes nothing itself: what it returns
// is made safe (TemplateHelper.Prepare), written as the template's draft and opened in the editor, where the owner checks
// it and saves or discards it. A template a run follows can be changed too, as a draft: ⤵ Apply refuses changes to the
// steps the run has reached, and the editor says so.
public partial class AdvancedTemplatesWindow
{
    private const double HelperWidth = 412; // the pane and its margin
    private double _helperGrewBy;
    private double? _minWidthWanted;
    private StackPanel _planButtons = null!;
    private TextBlock _helperTarget = null!;
    private IReadOnlyList<string> _plans = [];

    /// <summary>The Claude Code command the helper runs (from Settings → Agents); the main window sets it.</summary>
    public string HelperCommand { get; set; } = "claude";

    /// <summary>Whether the crab wanders and does tricks; the main window passes its settings.</summary>
    public bool CrabAnimated { set => Crab.SetAnimated(value); }

    private void InitHelper()
    {
        Helper.Actions = BuildHelperActions();
        Helper.Answerer = AnswerAsync;
        Helper.WorkingText = "Working on it…";
        Helper.AskToolTip = "Ask the Template helper (Enter). Uses your Claude login through Claude Code. It can read this project but changes nothing in it: a template it builds or changes goes to a draft that opens in the editor for you to save.";
        Crab.Poked += () => SetHelperOpen(!Crab.IsActive);
        Helper.CloseRequested += () => SetHelperOpen(false);
        Helper.BusyChanged += busy => Crab.IsBusy = busy;
        Loaded += (_, _) => UpdatePlans();
        Closed += (_, _) => Helper.Stop();
        Activated += (_, _) => UpdatePlans();
    }

    private void SetHelperOpen(bool open)
    {
        Crab.IsActive = open;
        if ((Helper.Visibility == Visibility.Visible) == open) return;
        Helper.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        if (open)
        {
            _helperGrewBy = Controls.WindowPlacement.Widen(this, HelperWidth);
            WantMinWidth(HelperWidth);
            Helper.FocusInput();
        }
        else
        {
            WantMinWidth(-HelperWidth);
            if (WindowState == WindowState.Normal && _helperGrewBy > 0) Width = Math.Max(MinWidth, Width - _helperGrewBy);
            _helperGrewBy = 0;
        }
    }

    // The pane adds its width to the window's minimum, never past the screen.
    private void WantMinWidth(double delta)
    {
        _minWidthWanted = (_minWidthWanted ?? MinWidth) + delta;
        MinWidth = Math.Min(_minWidthWanted.Value, Controls.WindowPlacement.WorkArea(this).Width);
    }

    // ---- What the helper offers above the conversation: the plans it found, and what a change goes to ----

    private StackPanel BuildHelperActions()
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = "Build from a plan", Foreground = Muted, FontSize = 11, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
        _planButtons = new StackPanel();
        panel.Children.Add(_planButtons);
        _helperTarget = new TextBlock { Foreground = Muted, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        panel.Children.Add(_helperTarget);
        return panel;
    }

    private void UpdatePlans()
    {
        var plans = TemplateHelper.FindPlans(Folder);
        if (plans.SequenceEqual(_plans) && _planButtons.Children.Count > 0) return;
        _plans = plans;
        _planButtons.Children.Clear();
        if (plans.Count == 0)
            _planButtons.Children.Add(new TextBlock
            {
                Text = "No plan file found at the top of the project or in docs/. Name the file in your request, or paste the plan.",
                Foreground = Faint, FontSize = 11, TextWrapping = TextWrapping.Wrap,
            });
        foreach (var plan in plans)
        {
            var button = new Button
            {
                Content = "📄 Build a template from " + plan,
                Background = ButtonFill,
                Foreground = ButtonText,
                Padding = new Thickness(10, 4, 10, 5),
                Margin = new Thickness(0, 0, 0, 4),
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                ToolTip = $"Have the helper read {plan} and build a new template from it: a stage per stage of the plan. It opens as a draft for you to check and save.",
            };
            AutomationProperties.SetName(button, "Build a template from " + plan);
            button.Click += (_, _) => Helper.Ask($"Build an advanced template from {plan}.");
            _planButtons.Children.Add(button);
        }

        Helper.Examples.Clear();
        Helper.Examples.Add(plans.Count > 0 ? $"Build a template from {plans[0]}" : "Build a template for a three-stage build, a branch per stage");
        Helper.Examples.Add("Add a code review step after each build");
        Helper.Examples.Add("Open a pull request after every stage instead of at the end");
        Helper.Examples.Add("What does an escalation point do?");
    }

    /// <summary>The line under the plan buttons: which template a change goes to.</summary>
    private void UpdateHelperTarget()
    {
        if (_helperTarget is null) return;
        var file = _shownFile;
        _helperTarget.Text = (file?.Template ?? file?.NewDraft) is { } t
            ? $"Changes go to {t.Name} ({(file!.HasDraft || _draftOf == file.Id ? "its draft" : "a new draft of it")}). Pick another template on the left to change that one."
            : "No template is picked: a request builds a new one.";
    }

    // ---- Asking ----

    /// <summary>The template on screen as the helper should see it: the draft being edited, else the waiting draft, else the saved file.</summary>
    private TemplateHelperTarget? CurrentTarget()
    {
        if (_shownFile is not { } file) return null;
        if (_draft is not null && _draftOf == file.Id) return new TemplateHelperTarget(file.Id, _draft.Clone(), IsDraft: true, file.IsNew);
        AdvancedTemplate? draft = null;
        try { draft = file.HasDraft ? _store.ReadDraft(file.Id) : null; }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException) { /* an unreadable draft: the saved file */ }
        if (draft is not null) return new TemplateHelperTarget(file.Id, draft, IsDraft: true, file.IsNew);
        return file.Template is { } saved ? new TemplateHelperTarget(file.Id, saved.Clone(), IsDraft: false, IsNew: false) : null;
    }

    private async Task<HelpAnswer> AnswerAsync(IReadOnlyList<HelpTurn> history, string question, string? state, CancellationToken cancellationToken)
    {
        // A change being typed goes to the draft first, so the helper is given what the owner sees.
        if (_draft is not null) await FlushDraftAsync();

        IReadOnlyList<AdvancedTemplateFile> files;
        try { files = _store.List(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new InvalidOperationException("Could not read " + _store.Folder + ": " + ex.Message); }
        var target = CurrentTarget();
        var run = target is not null && _shownRun is { } bound && bound.Orchestrator.Run is { } r ? (bound.Pane, r) : default;
        var runInfo = run.r is { } tr
            ? new TemplateHelperRun(run.Pane, tr.State.ToString().ToLowerInvariant(), tr.Progress, tr.State == RunState.Ready ? [] : tr.StartedStepIds)
            : null;
        var taken = TakenStepIds().Concat(target?.Template.AllSteps.Select(s => s.Id) ?? []).ToHashSet(StringComparer.Ordinal);
        var context = new TemplateHelperContext(Folder, files, target, runInfo, _plans, taken);
        var before = target?.Template.Serialize();

        StatusText.Text = "The Template helper is asking Claude Code…";
        TemplateHelperReply reply;
        try { reply = await TemplateHelper.RunAsync(new ProcessRunner(), TemplateHelper.Command(HelperCommand), context, history, question, cancellationToken); }
        catch (OperationCanceledException) { StatusText.Text = "The Template helper was stopped."; throw; }
        catch (InvalidOperationException) { StatusText.Text = "The Template helper failed; the reason is in its pane."; throw; }

        var answer = new StringBuilder(reply.Answer.Length > 0 ? reply.Answer : "Done.");
        if (reply.Action == TemplateHelperAction.None || reply.Template is not { } made)
        {
            if (reply.TemplateError is { } error) answer.Append("\n\nI tried to write a template, but it could not be read, so nothing was written: ").Append(error);
            StatusText.Text = "";
            return new HelpAnswer(answer.ToString(), reply.Suggestions);
        }

        var change = reply.Action == TemplateHelperAction.Change && target is not null;
        string id;
        if (change)
        {
            // The owner may have changed the template while the helper worked: the helper's version is not written over theirs.
            id = target!.Id;
            var now = id == _shownFile?.Id ? CurrentTarget()?.Template.Serialize() : null;
            if (now != before)
            {
                answer.Append($"\n\n**{target.Template.Name}** changed while I was working (or another template was picked), so I did not write my version over it. Ask me again and I'll work from it as it is now.");
                StatusText.Text = "The Template helper's change was not written: the template changed meanwhile.";
                return new HelpAnswer(answer.ToString(), reply.Suggestions);
            }
        }
        else id = TemplateHelper.NewTemplateId(made.Name, files.Select(f => f.Id));

        var fixes = TemplateHelper.Prepare(made, change ? target!.Template : null, change ? taken : []);
        made.Id = id;
        var editingOther = _draft is not null && _draftOf != id;
        if (_draft is not null && _draftOf == id) await StopEditingAsync();
        try { await _store.SaveDraftAsync(id, made); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = "The draft could not be written: " + ex.Message;
            throw new InvalidOperationException("I made the template but its draft could not be written: " + ex.Message);
        }

        // Show it: opened in the editor, unless the owner is editing another template, which is left alone.
        if (editingOther) Reload(SelectedId, force: true);
        else
        {
            Reload(id, force: true);
            if (_shownFile?.Id == id) StartEditing();
        }

        var steps = made.AllSteps.Count();
        var where = editingOther
            ? $"The draft of **{made.Name}** is waiting in the list on the left: pick it and ✏ Open the draft to look it over."
            : $"The draft of **{made.Name}** is open in the editor: {(made.Stages.Count == 1 ? "1 stage" : made.Stages.Count + " stages")}, {(steps == 1 ? "1 step" : steps + " steps")}. Look it over, change what you like, then 💾 Save it (or 🗑 Discard it).";
        answer.Append("\n\n").Append(where);
        var problems = made.Validate();
        if (problems.Count > 0)
            answer.Append($"\n\nIt has {(problems.Count == 1 ? "1 problem" : problems.Count + " problems")} to fix before it can be saved, listed above the tree: ").Append(string.Join(" ", problems));
        if (fixes.Count > 0) answer.Append("\n\nAgentHub corrected some ids: ").Append(string.Join(" ", fixes));
        if (change && run.r is { State: not RunState.Ready } followed && problems.Count == 0 && followed.ProblemsWith(made) is { Count: > 0 } runProblems)
            answer.Append($"\n\nThe run on {run.Pane} could not be given this version: ").Append(string.Join(" ", runProblems));
        StatusText.Text = $"The Template helper wrote {Path.GetFileName(_store.DraftPathFor(id))} · {DateTime.Now:HH:mm:ss}";
        return new HelpAnswer(answer.ToString(), reply.Suggestions);
    }
}
