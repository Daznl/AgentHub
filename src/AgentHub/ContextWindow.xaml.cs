using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AgentHub.Services.Instructions;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
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
        Activated += (_, _) => CheckDisk();
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

    // Picks up edits made elsewhere (an agent, another editor) whenever the window comes back into focus.
    private void CheckDisk()
    {
        if (_current is null || _loaded is null) return;
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
        base.OnClosing(e);
    }

    private static string CliName(InstructionCli cli) => cli switch
    {
        InstructionCli.ClaudeCode => "Claude Code",
        InstructionCli.Codex => "Codex",
        _ => "Gemini / Antigravity"
    };

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
