using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AgentHub.Models;
using AgentHub.Services;
using AgentHub.Services.Move;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using FontFamily = System.Windows.Media.FontFamily;
using MessageBox = System.Windows.MessageBox;
using TextBox = System.Windows.Controls.TextBox;

namespace AgentHub;

/// <summary>
/// Settings → Move to another computer: packs this computer's AgentHub data into one zip (<see cref="MoveExporter"/>)
/// or unpacks one from another computer (<see cref="MoveImporter"/>). Unpacking never overwrites; AgentHub's own
/// settings are merged at the next start (<see cref="SettingsImport"/>), so the window offers a restart.
/// </summary>
public partial class MoveWindow : Window
{
    private readonly AppSettings _settings;
    private readonly SettingsService _settingsService;
    private readonly GitService _git;
    private readonly Action _restart;
    private readonly MoveLocations _here;

    private List<MoveItemPlan> _plans = [];
    private readonly Dictionary<string, CheckBox> _packChecks = [];
    private readonly Dictionary<string, CheckBox> _unpackChecks = [];
    private readonly List<(PathMapping Mapping, TextBox To)> _mappingRows = [];
    private MoveManifest? _manifest;
    private MoveImportResult? _result;
    private string? _packedZip;
    private CancellationTokenSource? _work;

    private static readonly Brush Muted = Frozen(0x7E, 0x8C, 0xA6);
    private static readonly Brush Faint = Frozen(0x4A, 0x5A, 0x73);
    private static readonly Brush Good = Frozen(0x34, 0xE5, 0xA8);
    private static readonly Brush Bad = Frozen(0xFF, 0x7A, 0x45);

    public MoveWindow(AppSettings settings, SettingsService settingsService, GitService git, Action restart)
    {
        InitializeComponent();
        _settings = settings;
        _settingsService = settingsService;
        _git = git;
        _restart = restart;
        _here = MoveLocations.ForThisComputer(settings, settingsService.SettingsDirectory);
        PackPathBox.Text = Path.Combine(MoveLocations.ExportsRoot, MoveExporter.SuggestedFileName(DateTime.Now));
        ShowMode(pack: true);
        Loaded += async (_, _) => await ScanAsync();
        Closing += (_, e) =>
        {
            if (_work is null) return;
            if (MessageBox.Show(this, "Stop and close? A half-made zip is deleted; files already unpacked stay.", "AgentHub",
                    MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            {
                e.Cancel = true;
                return;
            }
            _work.Cancel();
        };
    }

    /// <summary>Opens on the unpack side with this package chosen (used by UI tests and a dropped zip).</summary>
    public void ShowPackage(string zipPath)
    {
        ShowMode(pack: false);
        ChoosePackage(zipPath);
    }

    private void PackMode_Click(object sender, RoutedEventArgs e) => ShowMode(pack: true);
    private void UnpackMode_Click(object sender, RoutedEventArgs e) => ShowMode(pack: false);

    private void ShowMode(bool pack)
    {
        PackModeBtn.IsChecked = pack;
        UnpackModeBtn.IsChecked = !pack;
        PackPanel.Visibility = pack ? Visibility.Visible : Visibility.Collapsed;
        UnpackPanel.Visibility = pack ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SetBusy(bool busy)
    {
        PackModeBtn.IsEnabled = UnpackModeBtn.IsEnabled = !busy;
        PackBtn.IsEnabled = !busy && _plans.Count > 0;
        UnpackBtn.IsEnabled = !busy && _manifest is not null && _result is null;
        PackCancelBtn.Visibility = UnpackCancelBtn.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        foreach (var box in _packChecks.Values.Concat(_unpackChecks.Values)) box.IsEnabled = !busy && box.Tag is true;
        foreach (var (_, to) in _mappingRows) to.IsEnabled = !busy;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _work?.Cancel();

    // ---------------- Pack ----------------

    private async Task ScanAsync()
    {
        PackStatus.Text = "Counting files…";
        PackBtn.IsEnabled = false;
        try
        {
            _plans = await Task.Run(() => MoveExporter.Scan(_here));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            PackStatus.Text = "Could not list the folders: " + ex.Message;
            return;
        }

        PackItems.Children.Clear();
        _packChecks.Clear();
        foreach (var plan in _plans)
        {
            var any = plan.Files.Count > 0;
            var box = new CheckBox { IsChecked = any, IsEnabled = any, Tag = any, Margin = new Thickness(0, 2, 10, 0), VerticalAlignment = VerticalAlignment.Top };
            System.Windows.Automation.AutomationProperties.SetName(box, plan.Item.Label);
            box.Click += (_, _) => UpdatePackTotal();
            _packChecks[plan.Item.Key] = box;
            PackItems.Children.Add(ItemRow(box, plan.Item, any ? $"{FormatSize(plan.Bytes)} · {Files(plan.Files.Count)}" : "nothing here",
                plan.Root));
        }
        UpdatePackTotal();
        PackStatus.Text = "";
        PackBtn.IsEnabled = true;
    }

    private static Border ItemRow(CheckBox box, MoveItem item, string size, string? folder)
    {
        var text = new StackPanel();
        var title = new DockPanel();
        var sizeText = new TextBlock { Text = size, FontSize = 12, Foreground = box.Tag is true ? Brushes.White : Faint, Margin = new Thickness(12, 0, 0, 0) };
        DockPanel.SetDock(sizeText, Dock.Right);
        title.Children.Add(sizeText);
        title.Children.Add(new TextBlock { Text = item.Label, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = box.Tag is true ? Brushes.White : Faint });
        text.Children.Add(title);
        text.Children.Add(new TextBlock { Text = item.Description, FontSize = 11.5, Foreground = Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
        if (folder is not null)
            text.Children.Add(new TextBlock { Text = folder, FontSize = 10.5, Foreground = Faint, FontFamily = new FontFamily("Cascadia Mono, Consolas"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0), ToolTip = folder });

        var row = new DockPanel();
        DockPanel.SetDock(box, Dock.Left);
        row.Children.Add(box);
        row.Children.Add(text);
        return new Border { Child = row, Padding = new Thickness(8, 7, 8, 7), BorderBrush = Frozen(0x16, 0x20, 0x2F), BorderThickness = new Thickness(0, 0, 0, 1) };
    }

    private List<MoveItemPlan> ChosenPlans() => _plans.Where(p => _packChecks.TryGetValue(p.Item.Key, out var b) && b.IsChecked == true).ToList();

    private void UpdatePackTotal()
    {
        var chosen = ChosenPlans();
        PackTotalText.Text = $"{FormatSize(chosen.Sum(p => p.Bytes))} · {Files(chosen.Sum(p => p.Files.Count))}";
        PackBtn.IsEnabled = chosen.Count > 0 && _work is null;
    }

    private void PackBrowse_Click(object sender, RoutedEventArgs e)
    {
        var current = PackPathBox.Text.Trim();
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save the move package",
            Filter = "Zip (*.zip)|*.zip",
            FileName = Path.GetFileName(current),
            InitialDirectory = Directory.Exists(Path.GetDirectoryName(current)) ? Path.GetDirectoryName(current) : _here.UserProfile,
        };
        if (dialog.ShowDialog(this) == true) PackPathBox.Text = dialog.FileName;
    }

    private async void Pack_Click(object sender, RoutedEventArgs e)
    {
        var zip = PackPathBox.Text.Trim();
        if (!zip.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || !Path.IsPathFullyQualified(zip))
        {
            PackStatus.Text = "Give a full path ending in .zip.";
            PackStatus.Foreground = Bad;
            return;
        }
        if (File.Exists(zip) && MessageBox.Show(this, $"{zip} already exists. Replace it?", "AgentHub", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;

        var plans = ChosenPlans();
        _work = new CancellationTokenSource();
        SetBusy(true);
        PackShowBtn.Visibility = Visibility.Collapsed;
        PackProgress.Visibility = Visibility.Visible;
        PackStatus.Foreground = Muted;
        var progress = new Progress<MoveProgress>(p =>
        {
            PackProgress.Value = p.BytesTotal == 0 ? 0 : (double)p.BytesDone / p.BytesTotal;
            PackStatus.Text = $"{p.Stage}: {FormatSize(p.BytesDone)} of {FormatSize(p.BytesTotal)} ({p.FilesDone:N0} of {p.FilesTotal:N0} files)";
        });
        try
        {
            // The pane layout and repositories as they are right now, not as last saved.
            _settings.PaneLayout = Owner is MainWindow main ? main.CurrentPaneLayout() : _settings.PaneLayout;
            await _settingsService.SaveAsync(_settings);
            var result = await MoveExporter.ExportAsync(zip, plans, _here, _settings, progress, _work.Token);
            _packedZip = result.ZipPath;
            PackStatus.Foreground = Good;
            PackStatus.Text = $"Packed {Files(result.Files)} ({FormatSize(result.SourceBytes)}) into {FormatSize(result.ZipBytes)}: {result.ZipPath}" +
                              (result.Skipped.Count > 0 ? $"\n{result.Skipped.Count} file(s) could not be read and were left out (listed in the zip's manifest.json)." : "") +
                              "\nSend it to yourself, then on the other computer: Settings → Move to another computer → Unpack a package.";
            PackShowBtn.Visibility = Visibility.Visible;
        }
        catch (OperationCanceledException)
        {
            PackStatus.Foreground = Muted;
            PackStatus.Text = "Stopped. No zip was left behind.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            PackStatus.Foreground = Bad;
            PackStatus.Text = "Could not pack: " + ex.Message;
        }
        finally
        {
            _work.Dispose();
            _work = null;
            PackProgress.Visibility = Visibility.Collapsed;
            SetBusy(false);
            UpdatePackTotal();
        }
    }

    private void PackShow_Click(object sender, RoutedEventArgs e)
    {
        if (_packedZip is not null && File.Exists(_packedZip))
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_packedZip}\"") { UseShellExecute = true });
    }

    // ---------------- Unpack ----------------

    private void UnpackBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose the move package",
            Filter = "AgentHub move package (*.zip)|*.zip",
            InitialDirectory = Directory.Exists(Path.Combine(_here.UserProfile, "Downloads")) ? Path.Combine(_here.UserProfile, "Downloads") : _here.UserProfile,
        };
        if (dialog.ShowDialog(this) == true) ChoosePackage(dialog.FileName);
    }

    private void ChoosePackage(string zip)
    {
        _manifest = null;
        _result = null;
        UnpackPathBox.Text = zip;
        UnpackChoices.Visibility = Visibility.Collapsed;
        UnpackResults.Visibility = Visibility.Collapsed;
        ReportBtn.Visibility = Visibility.Collapsed;
        UnpackStatus.Text = "";
        try
        {
            _manifest = MoveImporter.ReadManifest(zip);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            UnpackFromText.Text = ex.Message;
            UnpackFromText.Foreground = Bad;
            UnpackBtn.IsEnabled = false;
            return;
        }

        var m = _manifest;
        UnpackFromText.Foreground = Muted;
        UnpackFromText.Text = $"Packed on {m.Machine} by {m.UserName}, {m.CreatedUtc.ToLocalTime():d MMM yyyy HH:mm}: " +
                              $"{FormatSize(m.Items.Sum(i => i.Bytes))} in {Files(m.Items.Sum(i => i.Files))}.";

        UnpackItems.Children.Clear();
        _unpackChecks.Clear();
        foreach (var item in MoveLocations.Items)
        {
            var packed = m.Items.FirstOrDefault(i => i.Key == item.Key);
            var any = packed is { Files: > 0 };
            var box = new CheckBox { IsChecked = any, IsEnabled = any, Tag = any, Margin = new Thickness(0, 2, 10, 0), VerticalAlignment = VerticalAlignment.Top };
            System.Windows.Automation.AutomationProperties.SetName(box, item.Label);
            _unpackChecks[item.Key] = box;
            UnpackItems.Children.Add(ItemRow(box, item, any ? $"{FormatSize(packed!.Bytes)} · {Files(packed.Files)}" : "not in the package",
                any ? "→ " + _here.Root(item.Key) : null));
        }

        MappingRows.Children.Clear();
        _mappingRows.Clear();
        var mappings = MoveImporter.SuggestMappings(m, _here);
        MappingsHint.Text = mappings.Count == 0
            ? "The folders are the same on both computers, so no paths need rewriting."
            : "Paths from the other computer are rewritten to these folders: in file contents, and in the session folder names History and Resume look up. Change one if a folder lives somewhere else here.";
        foreach (var mapping in mappings)
        {
            var to = new TextBox { Text = mapping.To, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12, Padding = new Thickness(8, 5, 8, 5) };
            System.Windows.Automation.AutomationProperties.SetName(to, "New folder for " + mapping.From);
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var from = new TextBlock
            {
                Text = mapping.From, ToolTip = mapping.From, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12, Foreground = Muted,
                VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
            };
            var arrow = new TextBlock { Text = "→", Margin = new Thickness(10, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = Muted };
            Grid.SetColumn(arrow, 1);
            Grid.SetColumn(to, 2);
            grid.Children.Add(from);
            grid.Children.Add(arrow);
            grid.Children.Add(to);
            MappingRows.Children.Add(grid);
            _mappingRows.Add((mapping, to));
        }

        UnpackChoices.Visibility = Visibility.Visible;
        UnpackBtn.IsEnabled = true;
    }

    private async void Unpack_Click(object sender, RoutedEventArgs e)
    {
        if (_manifest is null) return;
        var zip = UnpackPathBox.Text;
        var items = _unpackChecks.Where(c => c.Value.IsChecked == true).Select(c => c.Key).ToHashSet();
        var mappings = _mappingRows.Select(r => r.Mapping with { To = r.To.Text.Trim() }).ToList();
        if (mappings.FirstOrDefault(m => !Path.IsPathFullyQualified(m.To)) is { } bad)
        {
            UnpackStatus.Foreground = Bad;
            UnpackStatus.Text = $"\"{bad.To}\" is not a full folder path.";
            return;
        }

        _work = new CancellationTokenSource();
        SetBusy(true);
        UnpackProgress.Visibility = Visibility.Visible;
        UnpackStatus.Foreground = Muted;
        var progress = new Progress<MoveProgress>(p =>
        {
            UnpackProgress.Value = p.BytesTotal == 0 ? 0 : (double)p.BytesDone / p.BytesTotal;
            UnpackStatus.Text = $"{p.Stage}: {FormatSize(p.BytesDone)} of {FormatSize(p.BytesTotal)} ({p.FilesDone:N0} of {p.FilesTotal:N0} files)";
        });
        try
        {
            _result = await MoveImporter.ImportAsync(zip, new MoveImportOptions { Target = _here, Mappings = mappings, Items = items }, progress, _work.Token);
            UnpackStatus.Text = "";
            ShowResult(_result, _manifest);
        }
        catch (OperationCanceledException)
        {
            UnpackStatus.Text = "Stopped. Files unpacked so far stay; unpacking again skips them.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            UnpackStatus.Foreground = Bad;
            UnpackStatus.Text = "Could not unpack: " + ex.Message;
        }
        finally
        {
            _work.Dispose();
            _work = null;
            UnpackProgress.Visibility = Visibility.Collapsed;
            SetBusy(false);
        }
    }

    private void ShowResult(MoveImportResult result, MoveManifest manifest)
    {
        UnpackChoices.Visibility = Visibility.Collapsed;
        UnpackResults.Visibility = Visibility.Visible;
        ReportBtn.Visibility = Visibility.Visible;
        RestartBanner.Visibility = result.SettingsPending ? Visibility.Visible : Visibility.Collapsed;

        var c = result.Counts.Values;
        var lines = new List<string>
        {
            $"{Files(c.Sum(x => x.Written))} added, {Plural(c.Sum(x => x.Extended), "session")} completed, {c.Sum(x => x.Same):N0} already here.",
        };
        if (result.Conflicts.Count > 0) lines.Add($"{result.Conflicts.Count:N0} differed from this computer's copy, which was kept (below).");
        var skipped = c.Sum(x => x.Skipped);
        if (skipped > 0) lines.Add($"{skipped:N0} could not be unpacked (see the report).");
        lines.Add("Report and set-aside copies: " + result.ImportDir);
        ResultSummary.Text = string.Join("\n", lines);

        ConflictRows.Children.Clear();
        ConflictSection.Visibility = result.Conflicts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var conflict in result.Conflicts.Take(200)) ConflictRows.Children.Add(ConflictRow(conflict, result.ImportDir));
        if (result.Conflicts.Count > 200)
            ConflictRows.Children.Add(new TextBlock { Text = $"…and {result.Conflicts.Count - 200} more, listed in the report.", Foreground = Muted, FontSize = 12, Margin = new Thickness(8, 6, 8, 6) });

        RepoRows.Children.Clear();
        RepoSection.Visibility = result.MissingRepositories.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var repo in result.MissingRepositories) RepoRows.Children.Add(RepoRow(repo));

        McpText.Visibility = manifest.McpServers.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        McpText.Text = $"Claude Code MCP servers to add again by hand (their settings can hold keys, so they were not packed): {string.Join(", ", manifest.McpServers)}.";
    }

    private Border ConflictRow(MoveConflict conflict, string importDir)
    {
        var status = new TextBlock { FontSize = 11, Foreground = Muted, Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap };
        var use = new Button { Content = "Use the imported copy", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(10, 0, 0, 0), FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Replace this computer's copy with the one from the other computer. This computer's copy is kept in the import folder (replaced\\)." };
        use.Click += (_, _) =>
        {
            try
            {
                var backup = MoveImporter.UseImportedCopy(conflict, importDir);
                use.IsEnabled = false;
                status.Text = "Imported copy in use. This computer's copy: " + backup;
                status.Foreground = Good;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                status.Text = "Could not replace it: " + ex.Message;
                status.Foreground = Bad;
            }
        };
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = conflict.TargetPath, ToolTip = conflict.TargetPath, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(status);
        var row = new DockPanel();
        DockPanel.SetDock(use, Dock.Right);
        row.Children.Add(use);
        row.Children.Add(text);
        return new Border { Child = row, Padding = new Thickness(8, 6, 8, 6), BorderBrush = Frozen(0x16, 0x20, 0x2F), BorderThickness = new Thickness(0, 0, 0, 1) };
    }

    private Border RepoRow(MoveMissingRepository repo)
    {
        var status = new TextBlock { Text = repo.LocalPath, ToolTip = repo.LocalPath, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 11, Foreground = Muted, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) };
        var clone = new Button { Content = "Clone", Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(10, 0, 0, 0), FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
            IsEnabled = !string.IsNullOrWhiteSpace(repo.RemoteUrl), ToolTip = repo.RemoteUrl is null ? "No remote is known for this repository" : "git clone " + repo.RemoteUrl };
        clone.Click += async (_, _) =>
        {
            clone.IsEnabled = false;
            status.Text = "Cloning…";
            var (code, _, err) = await _git.CloneAsync(repo.RemoteUrl!, repo.LocalPath);
            status.Foreground = code == 0 ? Good : Bad;
            status.Text = code == 0 ? "Cloned to " + repo.LocalPath : "Clone failed: " + err.Trim().Split('\n').LastOrDefault();
            clone.IsEnabled = code != 0;
        };
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = repo.Name, FontSize = 13, FontWeight = FontWeights.SemiBold });
        text.Children.Add(status);
        var row = new DockPanel();
        DockPanel.SetDock(clone, Dock.Right);
        row.Children.Add(clone);
        row.Children.Add(text);
        return new Border { Child = row, Padding = new Thickness(8, 6, 8, 6), BorderBrush = Frozen(0x16, 0x20, 0x2F), BorderThickness = new Thickness(0, 0, 0, 1) };
    }

    private void Report_Click(object sender, RoutedEventArgs e)
    {
        if (_result is not null && File.Exists(_result.ReportPath))
            Process.Start(new ProcessStartInfo(_result.ReportPath) { UseShellExecute = true });
    }

    private void Restart_Click(object sender, RoutedEventArgs e) => _restart();

    private static System.Windows.Media.SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private static string Files(int count) => Plural(count, "file");

    private static string Plural(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count:N0} {noun}s";

    internal static string FormatSize(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / (1024d * 1024 * 1024):0.0} GB",
        >= 1024 * 1024 => $"{bytes / (1024d * 1024):0} MB",
        >= 1024 => $"{bytes / 1024d:0} KB",
        _ => $"{bytes} B",
    };
}
