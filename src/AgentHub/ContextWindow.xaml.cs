using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AgentHub.Services;
using AgentHub.Services.Instructions;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using Button = System.Windows.Controls.Button;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MessageBox = System.Windows.MessageBox;
using RadioButton = System.Windows.Controls.RadioButton;

namespace AgentHub;

/// <summary>
/// Shows and edits the instruction files coding CLIs read: either each CLI's user-wide file (global context) or the files
/// at one folder's root (repo context). Edits are written to the real file on Save; a file changed elsewhere since it was
/// opened is never overwritten without asking.
/// </summary>
public partial class ContextWindow : Window
{
    private static readonly SolidColorBrush ExistsBack = Freeze("#064E3B"), ExistsFore = Freeze("#6EE7B7");
    private static readonly SolidColorBrush MissingBack = Freeze("#3B2F12"), MissingFore = Freeze("#FCD34D");

    private IReadOnlyList<InstructionFile> _files = [];
    private InstructionFile? _current;
    private LoadedInstructionFile? _loaded;
    private bool _loading;

    public ContextWindow()
    {
        InitializeComponent();
        Controls.DarkTitleBar.Apply(this);
        Controls.WindowPlacement.KeepOnScreen(this);
        Activated += (_, _) => CheckDisk();
        HelperLog.ItemsSource = _helperLog;
    }

    /// <summary>The folder shown by a repo context window; null for the global one.</summary>
    public string? Folder { get; private set; }

    private bool IsDirty => _loaded is not null && Editor.Text != _loaded.Text;

    /// <summary>Shows each CLI's user-wide instruction file, opening on <paramref name="preferred"/>'s.</summary>
    public void ShowGlobal(InstructionCli? preferred)
    {
        Folder = null;
        Title = "AgentHub - Context";
        HeadingText.Text = "Context";
        SubheadingText.Text = "Standing instructions each CLI loads in every folder. Saved edits apply to new sessions.";
        ShowFiles(InstructionFiles.GlobalFiles(), preferred);
    }

    /// <summary>
    /// Shows the instruction files at <paramref name="folder"/>'s root. Returns false, leaving the window as it was, when
    /// the user keeps unsaved edits to another folder's file.
    /// </summary>
    public bool ShowRepo(string folder, InstructionCli? preferred)
    {
        if (Folder is not null && !string.Equals(Folder, folder, StringComparison.OrdinalIgnoreCase) && !ConfirmLeave()) return false;
        var name = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } n ? n : folder;
        Folder = folder;
        Title = $"AgentHub - Repo context - {name}";
        HeadingText.Text = $"Repo context · {name}";
        SubheadingText.Text = folder;
        SubheadingText.ToolTip = folder;
        ShowFiles(InstructionFiles.RepoFiles(folder), preferred);
        return true;
    }

    private void ShowFiles(IReadOnlyList<InstructionFile> files, InstructionCli? preferred)
    {
        _files = files;
        // Open the preferred CLI's file; otherwise the first that exists; otherwise the first.
        var start = files.FirstOrDefault(f => f.Cli == preferred && File.Exists(f.Path))
                    ?? files.FirstOrDefault(f => f.Cli == preferred)
                    ?? files.FirstOrDefault(f => File.Exists(f.Path))
                    ?? files[0];
        _current = null;
        _loaded = null;
        BuildTabs(start);
        Open(start);
    }

    private void BuildTabs(InstructionFile selected)
    {
        TabsPanel.Children.Clear();
        foreach (var file in _files)
        {
            var tab = new RadioButton
            {
                Style = (Style)FindResource("FileTab"),
                Content = File.Exists(file.Path) ? file.Label : $"{file.Label} (none)",
                Tag = file,
                ToolTip = $"{file.Path}\n{file.Note}",
                IsChecked = file == selected
            };
            tab.Checked += Tab_Checked;
            TabsPanel.Children.Add(tab);
        }
    }

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading || (sender as FrameworkElement)?.Tag is not InstructionFile file || file == _current) return;
        if (!ConfirmLeave())
        {
            SelectTab(_current);
            return;
        }
        Open(file);
    }

    private void SelectTab(InstructionFile? file)
    {
        _loading = true;
        foreach (var tab in TabsPanel.Children.OfType<RadioButton>()) tab.IsChecked = Equals(tab.Tag, file);
        _loading = false;
    }

    private void Open(InstructionFile file, string? status = null)
    {
        LoadedInstructionFile loaded;
        try
        {
            loaded = InstructionFiles.Load(file.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"Could not read {file.Path}:\n{ex.Message}", "AgentHub", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // A review belongs to the text it was run on; opening a file (another folder, a reload) ends it.
        _reviewCts?.Cancel();
        if (ShowingReview) CloseReview();

        _current = file;
        _loaded = loaded;
        _loading = true;
        Editor.Text = loaded.Text;
        _loading = false;
        Editor.CaretIndex = 0;
        Editor.ScrollToHome();
        FilePathText.Text = file.Path;
        FilePathText.ToolTip = $"{file.Path}\n{file.Note}";
        RefreshState();
        StatusText.Text = status ?? file.Note;
        HelperTarget.Text = $"Suggests edits to {file.Label} · {(Folder is null ? "user-wide" : "this repository")}";
        SelectTab(file);
        Editor.Focus();
    }

    private void RefreshState()
    {
        if (_current is null || _loaded is null) return;
        FileStateText.Text = _loaded.Exists ? "FILE" : "NOT CREATED YET";
        FileStateBadge.Background = _loaded.Exists ? ExistsBack : MissingBack;
        FileStateText.Foreground = _loaded.Exists ? ExistsFore : MissingFore;
        EmptyHint.Text = Editor.Text.Length > 0 ? "" : _loaded.Exists
            ? "This file is empty. Type instructions here and press Save."
            : $"No {Path.GetFileName(_current.Path)} here yet. Type instructions here and press Save to create it.";
        var dirty = IsDirty;
        SaveBtn.Content = dirty ? "● Save" : "Save";
        SaveBtn.IsEnabled = dirty;
        ReloadBtn.IsEnabled = dirty || !_loaded.Exists || File.Exists(_current.Path);
    }

    private void Editor_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        RefreshState();
        if (IsDirty) StatusText.Text = "Unsaved edits. Ctrl+S saves.";
    }

    private void SaveCommand_Executed(object sender, ExecutedRoutedEventArgs e) => Save();
    private void Save_Click(object sender, RoutedEventArgs e) => Save();

    /// <summary>Writes the editor to the file. Returns true when the file now holds the editor's text.</summary>
    private bool Save(bool overwrite = false)
    {
        if (_current is not { } file || _loaded is not { } loaded) return false;
        if (!IsDirty) return true;

        SaveOutcome outcome;
        LoadedInstructionFile saved;
        try
        {
            outcome = InstructionFiles.Save(file.Path, Editor.Text, loaded, overwrite, out saved);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"Could not save {file.Path}:\n{ex.Message}", "AgentHub", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        if (outcome == SaveOutcome.ChangedOnDisk)
        {
            var choice = MessageBox.Show(this,
                $"{file.Label} was changed outside AgentHub after you opened it:\n{file.Path}\n\n" +
                "Yes: replace it with your version (the other change is lost).\n" +
                "No: discard your edits and load the file as it is now.\n" +
                "Cancel: keep editing without saving.",
                "AgentHub - File changed", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
            if (choice == MessageBoxResult.Yes) return Save(overwrite: true);
            if (choice == MessageBoxResult.No) Open(file, "Loaded the version on disk; your edits were discarded.");
            return false;
        }

        var created = !loaded.Exists;
        _loaded = saved;
        RefreshState();
        StatusText.Text = $"{(created ? "Created" : "Saved")} {DateTime.Now:HH:mm:ss}. New {CliName(file.Cli)} sessions will read it.";
        if (created) BuildTabs(file);
        return true;
    }

    private void Reload_Click(object sender, RoutedEventArgs e)
    {
        if (_current is null) return;
        if (IsDirty && MessageBox.Show(this, "Discard your unsaved edits and read the file again?", "AgentHub",
                MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
        Open(_current, "Reloaded from disk.");
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_current is null) return;
        var startInfo = File.Exists(_current.Path)
            ? new ProcessStartInfo("explorer.exe", $"/select,\"{_current.Path}\"")
            : new ProcessStartInfo("explorer.exe", $"\"{ExistingAncestor(Path.GetDirectoryName(_current.Path)!)}\"");
        Process.Start(startInfo);
    }

    // ---- Review: Claude Code suggests changes; the user ticks which go into the editor ----

    /// <summary>Command that starts Claude Code (from the configured agent, else <c>claude</c>).</summary>
    public string ReviewCommand { get; set; } = "claude";

    private CancellationTokenSource? _reviewCts;
    private IReadOnlyList<DiffSegment>? _reviewSegments;
    private List<ReviewHunk> _reviewHunks = [];

    private bool Reviewing => _reviewCts is not null;
    private bool ShowingReview => _reviewSegments is not null;

    private async void Review_Click(object sender, RoutedEventArgs e)
    {
        if (_reviewCts is { } running)
        {
            running.Cancel();
            return;
        }
        if (_current is not { } file) return;

        var text = Editor.Text;
        var cts = new CancellationTokenSource();
        _reviewCts = cts;
        SetReviewing(true);
        var started = DateTime.Now;
        void Tick() => StatusText.Text = $"Reviewing {file.Label} with Claude Code… {(int)(DateTime.Now - started).TotalSeconds} s. Small files take under a minute, large ones a few minutes.";
        var ticker = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        ticker.Tick += (_, _) => Tick();
        Tick();
        ticker.Start();
        try
        {
            var suggestion = await InstructionReview.RunAsync(new ProcessRunner(), ReviewCommand, file, text, Folder, cts.Token);
            if (_current == file) ShowReview(file, text, suggestion, fromHelper: false);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Review cancelled.";
        }
        catch (InvalidOperationException ex)
        {
            StatusText.Text = "Review failed.";
            MessageBox.Show(this, ex.Message, "AgentHub - Review", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            ticker.Stop();
            _reviewCts = null;
            cts.Dispose();
            SetReviewing(false);
        }
    }

    // The editor is frozen while Claude Code reads it, so the suggestions always apply to the text it saw.
    private void SetReviewing(bool reviewing, bool helper = false)
    {
        _helperRunning = reviewing && helper;
        Editor.IsReadOnly = reviewing;
        TabsPanel.IsEnabled = !reviewing && !ShowingReview;
        ReviewBtn.Content = reviewing && !helper ? "■ Cancel review" : "✨ Review";
        ReviewBtn.IsEnabled = !(reviewing && helper);
        OpenFolderBtn.IsEnabled = !reviewing;
        UpdateHelperSend();
        if (reviewing)
        {
            SaveBtn.IsEnabled = false;
            ReloadBtn.IsEnabled = false;
        }
        else RefreshState();
    }

    /// <summary>Shows the suggested edits with Apply boxes. Returns the plan, so the helper can say what came of its request.</summary>
    private ReviewPlan ShowReview(InstructionFile file, string original, ReviewSuggestion suggestion, bool fromHelper)
    {
        var plan = InstructionReview.Plan(original, suggestion.Edits);
        var hunks = plan.Segments.Where(s => s.IsChange).Select((s, i) => ReviewHunk.From(s, plan.Reasons[i])).ToList();
        if (hunks.Count == 0)
        {
            var why = plan.Unplaced.Count > 0
                ? $"{plan.Unplaced.Count} suggested edit{(plan.Unplaced.Count == 1 ? "" : "s")} did not match the file's text, so nothing changed."
                : "No changes suggested.";
            StatusText.Text = suggestion.Summary.Length > 0 ? $"{why} {suggestion.Summary}" : why;
            return plan;
        }

        _reviewSegments = plan.Segments;
        _reviewHunks = hunks;
        _reviewFromHelper = fromHelper;
        ReviewHeading.Text = fromHelper
            ? $"Suggested for your request: {hunks.Count} change{(hunks.Count == 1 ? "" : "s")} to {file.Label}"
            : $"Claude Code suggests {hunks.Count} change{(hunks.Count == 1 ? "" : "s")} to {file.Label}";
        ReviewSummary.Text = suggestion.Summary;
        ReviewSummary.Visibility = suggestion.Summary.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ReviewUnplaced.Text = plan.Unplaced.Count == 0 ? "" :
            $"⚠ {plan.Unplaced.Count} more suggestion{(plan.Unplaced.Count == 1 ? "" : "s")} quoted text that is not in the file exactly once, so " +
            $"{(plan.Unplaced.Count == 1 ? "it was" : "they were")} left out: " + string.Join("; ", plan.Unplaced.Select(u => u.Why));
        ReviewUnplaced.Visibility = plan.Unplaced.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        HunkList.ItemsSource = hunks;
        ReviewPanel.Visibility = Visibility.Visible;
        Editor.Visibility = Visibility.Hidden;
        EmptyHint.Visibility = Visibility.Hidden;
        EditButtons.Visibility = Visibility.Collapsed;
        ReviewButtons.Visibility = Visibility.Visible;
        TabsPanel.IsEnabled = false;
        UpdateApplyButton();
        UpdateHelperSend();
        StatusText.Text = "Untick any change you do not want, then apply. Nothing is saved until you press Save.";
        return plan;
    }

    /// <summary>
    /// Shows edits that came from elsewhere (the timeline review's rules for the user-wide file) with Apply boxes, on the
    /// Claude Code file of this window, exactly as a finished Review would. False when the user keeps unsaved edits to another file.
    /// </summary>
    public bool ShowSuggestions(ReviewSuggestion suggestion)
    {
        var file = _files.FirstOrDefault(f => f.Cli == InstructionCli.ClaudeCode) ?? _files.FirstOrDefault();
        if (file is null) return false;
        if (_current != file)
        {
            if (!ConfirmLeave()) return false;
            Open(file); // ends any running or shown review of the other file
        }
        else
        {
            _reviewCts?.Cancel();
            if (ShowingReview) CloseReview();
        }
        if (_current != file) return false; // the file could not be read
        ShowReview(file, Editor.Text, suggestion, fromHelper: false);
        return true;
    }

    private void HunkToggle_Click(object sender, RoutedEventArgs e) => UpdateApplyButton();

    private void UpdateApplyButton()
    {
        var chosen = _reviewHunks.Count(h => h.Accepted);
        ApplyReviewBtn.Content = chosen == _reviewHunks.Count ? "Apply all" : $"Apply {chosen} of {_reviewHunks.Count}";
        ApplyReviewBtn.IsEnabled = chosen > 0;
    }

    private void ApplyReview_Click(object sender, RoutedEventArgs e)
    {
        if (_reviewSegments is not { } segments) return;
        var chosen = _reviewHunks.Count(h => h.Accepted);
        var merged = LineDiff.Merge(segments, i => _reviewHunks[i].Accepted);
        var fromHelper = _reviewFromHelper;
        CloseReview();
        Editor.Text = merged;
        Editor.Focus();
        StatusText.Text = $"Applied {chosen} of {_reviewHunks.Count} suggested change{(_reviewHunks.Count == 1 ? "" : "s")}. Check the editor, then Save.";
        if (fromHelper) Say($"Put {chosen} of {_reviewHunks.Count} into the editor. Press Save (Ctrl+S) to write {_current?.Label}, or ask for more.");
    }

    private void DiscardReview_Click(object sender, RoutedEventArgs e)
    {
        if (_reviewFromHelper) Say("Discarded; the editor is unchanged. Tell me what to do differently.");
        CloseReview();
        StatusText.Text = "Review discarded; the editor is unchanged.";
    }

    private void CloseReview()
    {
        _reviewSegments = null;
        HunkList.ItemsSource = null;
        ReviewPanel.Visibility = Visibility.Collapsed;
        Editor.Visibility = Visibility.Visible;
        EmptyHint.Visibility = Visibility.Visible;
        EditButtons.Visibility = Visibility.Visible;
        ReviewButtons.Visibility = Visibility.Collapsed;
        TabsPanel.IsEnabled = true;
        _reviewFromHelper = false;
        UpdateHelperSend();
    }

    // ---- Context helper (🦀): ask in plain words; the edits show in the review panel like a Review's ----

    private const double HelperWidth = 330;
    private const string HelperKeysHint = "Enter sends · Shift+Enter new line";
    private readonly ObservableCollection<HelperMessage> _helperLog = [];
    private bool _helperRunning;
    private bool _reviewFromHelper;
    private double _helperGrewBy;

    private void Crab_Click(object sender, RoutedEventArgs e) => SetHelperOpen(CrabBtn.IsChecked == true);

    private void CloseHelper_Click(object sender, RoutedEventArgs e) => SetHelperOpen(false);

    // The window widens by the panel so the editor keeps its size, and narrows again on close; a maximised window stays as it is.
    private void SetHelperOpen(bool open)
    {
        CrabBtn.IsChecked = open;
        if ((HelperPanel.Visibility == Visibility.Visible) == open) return;
        HelperPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        if (open)
        {
            if (WindowState == WindowState.Normal)
            {
                var area = SystemParameters.WorkArea;
                var width = Math.Min(Width + HelperWidth, area.Width);
                _helperGrewBy = width - Width;
                Width = width;
                if (Left + Width > area.Right) Left = Math.Max(area.Left, area.Right - Width);
            }
            MinWidth += HelperWidth;
            UpdateHelperSend();
            HelperInput.Focus();
        }
        else
        {
            MinWidth -= HelperWidth;
            if (WindowState == WindowState.Normal && _helperGrewBy > 0) Width = Math.Max(MinWidth, Width - _helperGrewBy);
            _helperGrewBy = 0;
        }
    }

    private void Example_Click(object sender, RoutedEventArgs e)
    {
        HelperInput.Text = (sender as Button)?.Content as string ?? "";
        HelperInput.CaretIndex = HelperInput.Text.Length;
        HelperInput.Focus();
    }

    private void HelperInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        HelperInputHint.Visibility = HelperInput.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateHelperSend();
    }

    // Enter sends, as in a chat; Shift+Enter is a new line.
    private void HelperInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;
        e.Handled = true;
        if (!_helperRunning && HelperSendBtn.IsEnabled) HelperSend_Click(HelperSendBtn, e);
    }

    // One request or review at a time, and never over suggestions still waiting to be applied or discarded.
    private void UpdateHelperSend()
    {
        if (_helperRunning)
        {
            HelperSendBtn.Content = "■ Cancel";
            HelperSendBtn.IsEnabled = true;
            HelperHint.Text = "Working on it. You can type your next request.";
            return;
        }
        var blocked = ShowingReview ? "Apply or discard the suggestions on the left first."
            : Reviewing ? "Waiting for ✨ Review to finish."
            : null;
        HelperSendBtn.Content = "Suggest";
        HelperSendBtn.IsEnabled = blocked is null && _current is not null && HelperInput.Text.Trim().Length > 0;
        HelperHint.Text = blocked ?? HelperKeysHint;
    }

    private async void HelperSend_Click(object sender, RoutedEventArgs e)
    {
        if (_helperRunning)
        {
            _reviewCts?.Cancel();
            return;
        }
        if (_current is not { } file || Reviewing || ShowingReview) return;
        var ask = HelperInput.Text.Trim();
        if (ask.Length == 0) return;

        HelperInput.Text = "";
        HelperExamples.Visibility = Visibility.Collapsed;
        _helperLog.Add(HelperMessage.User(ask));
        var reply = HelperMessage.Crab("");
        _helperLog.Add(reply);
        ScrollHelperToEnd();

        var text = Editor.Text;
        var cts = new CancellationTokenSource();
        _reviewCts = cts;
        SetReviewing(true, helper: true);
        var started = DateTime.Now;
        void Tick()
        {
            var seconds = (int)(DateTime.Now - started).TotalSeconds;
            reply.Text = $"Working out the edits to {file.Label}… {seconds} s";
            StatusText.Text = $"Context helper is asking Claude Code… {seconds} s. Usually under a minute.";
        }
        var ticker = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        ticker.Tick += (_, _) => Tick();
        Tick();
        ticker.Start();
        try
        {
            var suggestion = await InstructionRequest.RunAsync(new ProcessRunner(), ReviewCommand, file, text, Folder, ask, cts.Token);
            if (_current != file)
            {
                reply.Text = $"Another file was opened before the answer came back, so it was dropped. Open {file.Label} and ask again.";
                return;
            }
            var plan = ShowReview(file, text, suggestion, fromHelper: true);
            reply.Text = Describe(suggestion, plan);
        }
        catch (OperationCanceledException)
        {
            reply.Text = "Cancelled.";
            StatusText.Text = "Context helper cancelled.";
        }
        catch (InvalidOperationException ex)
        {
            reply.Text = ex.Message;
            reply.IsError = true;
            StatusText.Text = "Context helper failed; the reason is in the helper panel.";
        }
        finally
        {
            ticker.Stop();
            _reviewCts = null;
            cts.Dispose();
            SetReviewing(false);
            ScrollHelperToEnd();
        }
    }

    private static string Describe(ReviewSuggestion suggestion, ReviewPlan plan)
    {
        var count = plan.Reasons.Count;
        var lines = new List<string>();
        if (suggestion.Summary.Length > 0) lines.Add(suggestion.Summary);
        if (count > 0) lines.Add($"{count} change{(count == 1 ? " is" : "s are")} on the left. Untick any you do not want, then Apply.");
        else if (plan.Unplaced.Count > 0) lines.Add("The suggested edits did not match the file's text, so nothing changed. Try asking again.");
        else if (lines.Count == 0) lines.Add("Nothing to change for that.");
        if (count > 0 && plan.Unplaced.Count > 0)
            lines.Add($"{plan.Unplaced.Count} more did not match the file's text and {(plan.Unplaced.Count == 1 ? "was" : "were")} left out.");
        return string.Join("\n\n", lines);
    }

    private void Say(string text)
    {
        _helperLog.Add(HelperMessage.Crab(text));
        ScrollHelperToEnd();
    }

    private void ScrollHelperToEnd() =>
        Dispatcher.BeginInvoke(HelperScroll.ScrollToEnd, System.Windows.Threading.DispatcherPriority.Background);

    // Picks up edits made elsewhere (an agent, another editor) whenever the window comes back into focus.
    // Not during a review: the suggestions belong to the text that was sent, and Save still catches the change on disk.
    private void CheckDisk()
    {
        if (_current is null || _loaded is null || Reviewing || ShowingReview) return;
        if (InstructionFiles.CurrentHash(_current.Path) == _loaded.Hash) return;
        if (IsDirty)
            StatusText.Text = "⚠ The file changed on disk since you opened it. Saving will ask what to keep.";
        else
            Open(_current, $"Reloaded: the file changed on disk ({DateTime.Now:HH:mm:ss}).");
    }

    /// <summary>Asks what to do with unsaved edits. True when it is fine to move on.</summary>
    private bool ConfirmLeave()
    {
        if (!IsDirty || _current is null) return true;
        var choice = MessageBox.Show(this, $"Save your edits to {_current.Label} first?\n{_current.Path}", "AgentHub - Unsaved edits",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
        return choice switch
        {
            MessageBoxResult.Yes => Save(),
            MessageBoxResult.No => true,
            _ => false
        };
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!ConfirmLeave()) e.Cancel = true;
        else _reviewCts?.Cancel();
        base.OnClosing(e);
    }

    private static string CliName(InstructionCli cli) => InstructionReview.CliName(cli);

    private static string ExistingAncestor(string dir)
    {
        while (!Directory.Exists(dir) && Path.GetDirectoryName(dir) is { } parent) dir = parent;
        return dir;
    }

    private static SolidColorBrush Freeze(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}

/// <summary>One line of the context helper's conversation: the user's request (right) or the crab's reply (left).</summary>
public sealed class HelperMessage : INotifyPropertyChanged
{
    private static readonly SolidColorBrush UserBack = Freeze("#12324A"), UserBorder = Freeze("#24506B"), UserFore = Freeze("#E8F1FB");
    private static readonly SolidColorBrush CrabBack = Freeze("#1E140E"), CrabBorder = Freeze("#4A2A18"), CrabFore = Freeze("#EADFD6");
    private static readonly SolidColorBrush ErrorBack = Freeze("#2A1218"), ErrorBorder = Freeze("#5B2230"), ErrorFore = Freeze("#FCA5A5");

    private string _text = "";
    private bool _isError;

    private HelperMessage(bool fromUser) => FromUser = fromUser;

    public static HelperMessage User(string text) => new(true) { Text = text };
    public static HelperMessage Crab(string text) => new(false) { Text = text };

    public bool FromUser { get; }

    public string Text
    {
        get => _text;
        set { _text = value; Changed(nameof(Text)); }
    }

    public bool IsError
    {
        get => _isError;
        set { _isError = value; Changed(nameof(Background)); Changed(nameof(Border)); Changed(nameof(Foreground)); }
    }

    public HorizontalAlignment Alignment => FromUser ? HorizontalAlignment.Right : HorizontalAlignment.Left;
    public Thickness Indent => FromUser ? new Thickness(36, 0, 0, 10) : new Thickness(0, 0, 0, 10);
    public Visibility CrabVisibility => FromUser ? Visibility.Collapsed : Visibility.Visible;
    public SolidColorBrush Background => FromUser ? UserBack : IsError ? ErrorBack : CrabBack;
    public SolidColorBrush Border => FromUser ? UserBorder : IsError ? ErrorBorder : CrabBorder;
    public SolidColorBrush Foreground => FromUser ? UserFore : IsError ? ErrorFore : CrabFore;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private static SolidColorBrush Freeze(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}

/// <summary>One suggested change in the review list: the lines it removes and adds, and whether the user keeps it.</summary>
public sealed class ReviewHunk
{
    public required string Title { get; init; }
    public required string Why { get; init; }
    public required string RemovedText { get; init; }
    public required string AddedText { get; init; }
    public bool Accepted { get; set; } = true;

    public Visibility WhyVisibility => Why.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RemovedVisibility => RemovedText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility AddedVisibility => AddedText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public static ReviewHunk From(DiffSegment change, string why)
    {
        var removed = change.OldLines.Count;
        var title = removed switch
        {
            0 => change.OldStart == 1 ? "Added at the top" : $"Added after line {change.OldStart - 1}",
            1 => $"Line {change.OldStart}",
            _ => $"Lines {change.OldStart}–{change.OldStart + removed - 1}"
        };
        var what = (removed, change.NewLines.Count) switch
        {
            (0, _) => "added",
            (_, 0) => "removed",
            _ => "changed"
        };
        return new ReviewHunk
        {
            Title = removed == 0 ? title : $"{title} · {what}",
            Why = why,
            RemovedText = string.Join("\n", change.OldLines.Select(l => "− " + l)),
            AddedText = string.Join("\n", change.NewLines.Select(l => "+ " + l))
        };
    }
}
