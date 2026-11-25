using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AgentHub.Services.Instructions;
using AgentHub.Services.Todo;
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MessageBox = System.Windows.MessageBox;
using TextBox = System.Windows.Controls.TextBox;

namespace AgentHub;

/// <summary>
/// A project's to-do list: the Markdown task file at the folder's root (TODO.md, the one Claude Code writes), shown as
/// tasks that can be ticked, edited, added, removed and moved up or down to set the priority. Saved through the same
/// guarded write as the Context window: a file changed elsewhere since it was opened is never replaced without asking.
/// A missing file is created on the first save.
/// </summary>
public partial class TodoWindow : Window
{
    private static readonly Brush DoneText = Freeze("#5F6F89"), OpenText = Freeze("#E6EDF7");
    private static readonly Brush ExistsBack = Freeze("#064E3B"), ExistsFore = Freeze("#6EE7B7");
    private static readonly Brush MissingBack = Freeze("#3B2F12"), MissingFore = Freeze("#FCD34D");

    private string _path = "";
    private LoadedInstructionFile _loaded = new("", false, "missing", false, "\r\n");
    private TodoDocument _doc = new();
    private bool _rebuilding;

    public TodoWindow()
    {
        InitializeComponent();
        Controls.DarkTitleBar.Apply(this);
        Controls.WindowPlacement.KeepOnScreen(this);
        Activated += (_, _) => CheckDisk();
        Closing += TodoWindow_Closing;
    }

    /// <summary>The folder whose list this window shows.</summary>
    public string? Folder { get; private set; }

    private string _baseline = "";

    private bool IsDirty => _doc.Render() != _baseline;

    /// <summary>Shows the folder's list. Returns false, leaving the window as it was, when unsaved changes to another folder's list are kept.</summary>
    public bool ShowFolder(string folder)
    {
        if (Folder is not null && !string.Equals(Folder, folder, StringComparison.OrdinalIgnoreCase) && !ConfirmLeave()) return false;
        var name = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } n ? n : folder;
        Folder = folder;
        Title = $"AgentHub - To do - {name}";
        HeadingText.Text = $"To do · {name}";
        SubheadingText.Text = folder;
        SubheadingText.ToolTip = folder;
        _path = TodoDocument.FindPath(folder);
        return Load(null);
    }

    private bool Load(string? status)
    {
        try
        {
            _loaded = InstructionFiles.Load(_path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"Could not read {_path}:\n{ex.Message}", "AgentHub", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        _doc = TodoDocument.Parse(_loaded.Text);
        _baseline = _doc.Render();
        FilePathText.Text = _path;
        FilePathText.ToolTip = _path;
        Rebuild();
        StatusText.Text = status ?? (_loaded.Exists
            ? "Move tasks with ▲ ▼ to set the priority; the top one is next. Claude Code reads this file too."
            : "No to-do file here yet. Add a task and press Save to create it.");
        return true;
    }

    private void Rebuild(TodoTask? focus = null)
    {
        _rebuilding = true;
        TaskList.Children.Clear();
        var tasks = _doc.Tasks;
        TextBox? toFocus = null;
        for (var i = 0; i < tasks.Count; i++)
        {
            var row = BuildRow(tasks[i], i == 0, i == tasks.Count - 1, out var box);
            if (ReferenceEquals(tasks[i], focus)) toFocus = box;
            TaskList.Children.Add(row);
        }
        EmptyHint.Visibility = tasks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _rebuilding = false;
        RefreshState();
        toFocus?.Focus();
    }

    private UIElement BuildRow(TodoTask task, bool first, bool last, out TextBox box)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var title = task.Text.Length > 60 ? task.Text[..60] + "…" : task.Text;
        var check = new CheckBox
        {
            IsChecked = task.Done,
            Margin = new Thickness(6, 0, 10, 0),
            ToolTip = "Done",
            VerticalAlignment = VerticalAlignment.Center,
        };
        System.Windows.Automation.AutomationProperties.SetName(check, "Done: " + title);

        var text = new TextBox
        {
            Text = task.Text,
            FontSize = 13,
            MinHeight = 28,
            Margin = new Thickness(0, 0, 8, 0),
            Foreground = task.Done ? DoneText : OpenText,
            TextDecorations = task.Done ? TextDecorations.Strikethrough : null,
            ToolTip = task.Indent.Length > 0 ? "Sub-task" : null,
        };
        System.Windows.Automation.AutomationProperties.SetName(text, "Task: " + title);
        text.TextChanged += (_, _) =>
        {
            if (_rebuilding) return;
            task.Text = text.Text;
            RefreshState();
        };
        check.Click += (_, _) =>
        {
            task.Done = check.IsChecked == true;
            Rebuild(task);
        };

        Button Tool(string content, string name, string tip, bool enabled, Action click, int column)
        {
            var b = new Button { Content = content, ToolTip = tip, IsEnabled = enabled, Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 0, 4, 0), FontSize = 11, FontWeight = FontWeights.Bold, MinWidth = 28 };
            System.Windows.Automation.AutomationProperties.SetName(b, $"{name}: {title}");
            b.Click += (_, _) => click();
            Grid.SetColumn(b, column);
            grid.Children.Add(b);
            return b;
        }

        Grid.SetColumn(check, 0);
        Grid.SetColumn(text, 1);
        grid.Children.Add(check);
        grid.Children.Add(text);
        Tool("▲", "Move up", "Raise the priority: move this task up", !first, () => { _doc.Move(task, -1); Rebuild(task); }, 2);
        Tool("▼", "Move down", "Lower the priority: move this task down", !last, () => { _doc.Move(task, +1); Rebuild(task); }, 3);
        var remove = Tool("✕", "Remove", "Remove this task", true, () => { _doc.Remove(task); Rebuild(); }, 4);
        remove.Margin = new Thickness(0);
        box = text;
        return grid;
    }

    private void RefreshState()
    {
        FileStateText.Text = _loaded.Exists ? "FILE" : "NOT CREATED YET";
        FileStateBadge.Background = _loaded.Exists ? ExistsBack : MissingBack;
        FileStateText.Foreground = _loaded.Exists ? ExistsFore : MissingFore;
        var dirty = IsDirty;
        SaveBtn.Content = dirty ? "● Save" : "Save";
        SaveBtn.IsEnabled = dirty;
        ReloadBtn.IsEnabled = dirty || _loaded.Exists;
        if (dirty && !_rebuilding) StatusText.Text = "Unsaved changes. Ctrl+S saves.";
    }

    private void Add_Click(object sender, RoutedEventArgs e) => AddNew();

    private void NewTaskBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        AddNew();
    }

    private void AddNew()
    {
        var text = NewTaskBox.Text.Trim();
        if (text.Length == 0) return;
        // A task is one line: a pasted line break would split it into notes in the file.
        text = string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        _doc.Add(text);
        NewTaskBox.Clear();
        Rebuild();
        NewTaskBox.Focus();
    }

    private void SaveCommand_Executed(object sender, ExecutedRoutedEventArgs e) => Save();
    private void Save_Click(object sender, RoutedEventArgs e) => Save();

    /// <summary>Writes the list to the file. Returns true when the file now holds it.</summary>
    private bool Save(bool overwrite = false)
    {
        if (!IsDirty) return true;
        // Empty task text would write "- [ ] "; drop such rows rather than save blanks.
        foreach (var blank in _doc.Tasks.Where(t => string.IsNullOrWhiteSpace(t.Text)).ToList()) _doc.Remove(blank);

        var text = _doc.Render();
        SaveOutcome outcome;
        LoadedInstructionFile saved;
        try
        {
            outcome = InstructionFiles.Save(_path, text, _loaded, overwrite, out saved);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"Could not save {_path}:\n{ex.Message}", "AgentHub", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        if (outcome == SaveOutcome.ChangedOnDisk)
        {
            var choice = MessageBox.Show(this,
                $"{Path.GetFileName(_path)} was changed outside AgentHub after you opened it:\n{_path}\n\n" +
                "Yes: replace it with your version (the other change is lost).\n" +
                "No: discard your changes and load the file as it is now.\n" +
                "Cancel: keep editing without saving.",
                "AgentHub - File changed", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
            if (choice == MessageBoxResult.Yes) return Save(overwrite: true);
            if (choice == MessageBoxResult.No) Load("Loaded the version on disk; your changes were discarded.");
            return false;
        }

        var created = !_loaded.Exists;
        _loaded = saved;
        _baseline = text;
        Rebuild();
        StatusText.Text = $"{(created ? "Created" : "Saved")} {DateTime.Now:HH:mm:ss}.";
        return true;
    }

    private void Reload_Click(object sender, RoutedEventArgs e)
    {
        if (IsDirty && MessageBox.Show(this, "Discard your unsaved changes and read the file again?", "AgentHub",
                MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
        Load("Reloaded from disk.");
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var startInfo = File.Exists(_path)
            ? new ProcessStartInfo("explorer.exe", $"/select,\"{_path}\"")
            : new ProcessStartInfo("explorer.exe", $"\"{Folder}\"");
        Process.Start(startInfo);
    }

    /// <summary>Claude Code (or anyone) may edit the file while the window is open; with nothing unsaved, show the new version.</summary>
    private void CheckDisk()
    {
        if (Folder is null || IsDirty) return;
        if (InstructionFiles.CurrentHash(_path) != _loaded.Hash) Load("The file changed on disk; showing the new version.");
    }

    private bool ConfirmLeave()
    {
        if (!IsDirty) return true;
        var choice = MessageBox.Show(this, "Save your changes to the to-do list first?", "AgentHub - To do",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
        return choice switch
        {
            MessageBoxResult.Yes => Save(),
            MessageBoxResult.No => true,
            _ => false,
        };
    }

    private void TodoWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!ConfirmLeave()) e.Cancel = true;
    }

    private static SolidColorBrush Freeze(string hex)
    {
        var brush = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
