using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AgentHub.Models;
using AgentHub.Services.Context;
using AgentHub.Services.History;
using AgentHub.Terminal;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using FontStyle = System.Windows.FontStyle;

namespace AgentHub;

/// <summary>
/// Every Claude Code and Codex session per repository, with the full history of each: prompts, replies, thinking, tool
/// calls and results. Read from the CLIs' own transcripts; the open session is followed live while it is written.
/// </summary>
public partial class HistoryWindow : Window
{
    private readonly ObservableCollection<LaunchLocation> _locations = [];
    private ObservableCollection<HistoryEntryRow> _entries = [];
    private ListCollectionView? _view;
    private readonly DispatcherTimer _liveTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private SessionRow? _session;
    private long _offset;
    private int _sessionGeneration;
    private int _locationGeneration;
    private bool _reading;
    private bool _atBottom = true;
    private string? _pendingSessionPath;
    private bool _pendingFollow;
    private int _ticks;

    public HistoryWindow(IEnumerable<RepositoryDefinition> repositories)
    {
        InitializeComponent();
        LocationList.ItemsSource = _locations;
        UpdateRepositories(repositories);
        _liveTimer.Tick += async (_, _) => await LiveTickAsync();
        _liveTimer.Start();
        Closed += (_, _) => _liveTimer.Stop();
        Loaded += (_, _) =>
        {
            if (LocationList.SelectedItem is null && _locations.Count > 0) LocationList.SelectedIndex = 0;
            EntryScroller()?.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(EntryList_ScrollChanged));
        };
        ShowEntryMessage("Pick a repository, then a session.");
    }

    /// <summary>Home first, then registered repositories by name. Folders opened from a pane stay in the list.</summary>
    public void UpdateRepositories(IEnumerable<RepositoryDefinition> repositories)
    {
        var selected = LocationList.SelectedItem as LaunchLocation;
        var extra = _locations.Where(l => l.Repository is null && l.Path != TerminalPaneControl.BaseDirectory).ToList();

        _locations.Clear();
        _locations.Add(new LaunchLocation { Name = "🏠 Home", Path = TerminalPaneControl.BaseDirectory });
        foreach (var e in extra) _locations.Add(e);
        foreach (var r in repositories.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
            _locations.Add(new LaunchLocation { Name = r.Name, Path = r.LocalPath, Repository = r });

        if (selected is not null)
            LocationList.SelectedItem = _locations.FirstOrDefault(l => SamePath(l.Path, selected.Path));
    }

    /// <summary>Shows <paramref name="folder"/>'s sessions and, when given, opens that transcript scrolled to the newest entry.</summary>
    public void Navigate(string folder, string? sessionPath)
    {
        var location = _locations.FirstOrDefault(l => SamePath(l.Path, folder));
        if (location is null)
        {
            var name = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            location = new LaunchLocation { Name = "📁 " + (string.IsNullOrWhiteSpace(name) ? folder : name), Path = folder };
            _locations.Insert(1, location);
        }

        _pendingSessionPath = sessionPath;
        _pendingFollow = true;
        if (LocationList.SelectedItem == location) _ = LoadSessionsAsync(location);
        else LocationList.SelectedItem = location;
        LocationList.ScrollIntoView(location);
    }

    // ---- sessions ----

    private async void LocationList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LocationList.SelectedItem is LaunchLocation location) await LoadSessionsAsync(location);
    }

    private async void RefreshSessions_Click(object sender, RoutedEventArgs e)
    {
        if (LocationList.SelectedItem is LaunchLocation location) await LoadSessionsAsync(location);
    }

    private async Task LoadSessionsAsync(LaunchLocation location, bool quiet = false)
    {
        var generation = ++_locationGeneration;
        if (!quiet)
        {
            SessionCountText.Text = "Scanning transcripts…";
            SessionEmptyText.Visibility = Visibility.Collapsed;
        }

        var sessions = await Task.Run(() => SessionHistoryReader.ListSessions(location.Path));
        if (generation != _locationGeneration) return;

        var keep = _pendingSessionPath ?? (quiet ? _session?.FilePath : null);
        var rows = sessions.Select(s => new SessionRow(s)).ToList();
        SessionList.ItemsSource = rows;

        var claude = sessions.Count(s => s.Agent == ContextSource.ClaudeCode);
        var codex = sessions.Count - claude;
        SessionCountText.Text = sessions.Count == 0
            ? "No sessions"
            : $"{sessions.Count} session{(sessions.Count == 1 ? "" : "s")} · {claude} Claude Code · {codex} Codex";
        SessionEmptyText.Text = $"No Claude Code or Codex sessions recorded in\n{location.Path}";
        SessionEmptyText.Visibility = sessions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var match = keep is null ? null : rows.FirstOrDefault(r => SamePath(r.FilePath, keep));
        if (match is not null)
        {
            if (_session is not null && SamePath(_session.FilePath, match.FilePath) && _pendingSessionPath is null)
            {
                // Quiet refresh of the open session: keep reading where we are.
                _session = match;
                SessionList.SelectionChanged -= SessionList_SelectionChanged;
                SessionList.SelectedItem = match;
                SessionList.SelectionChanged += SessionList_SelectionChanged;
            }
            else SessionList.SelectedItem = match;
            SessionList.ScrollIntoView(match);
        }
        else if (!quiet)
        {
            if (rows.Count > 0) SessionList.SelectedIndex = 0;
            else ClearSession("No sessions in this location yet.");
        }
        _pendingSessionPath = null;
    }

    private async void SessionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SessionList.SelectedItem is SessionRow row) await OpenSessionAsync(row, follow: _pendingFollow);
        _pendingFollow = false;
    }

    private void ClearSession(string message)
    {
        _sessionGeneration++;
        _session = null;
        _entries = [];
        _view = null;
        EntryList.ItemsSource = null;
        SessionTitleText.Text = "Session history";
        SessionMetaText.Text = "Pick a repository, then a session.";
        ExportBtn.IsEnabled = OpenTranscriptBtn.IsEnabled = false;
        EntryCountText.Text = "";
        JumpLatestBtn.Visibility = Visibility.Collapsed;
        ShowEntryMessage(message);
    }

    // ---- entries ----

    private async Task OpenSessionAsync(SessionRow row, bool follow)
    {
        var generation = ++_sessionGeneration;
        _session = row;
        _offset = 0;
        SessionTitleText.Text = row.Title;
        SessionMetaText.Text = $"{row.AgentName} · started {row.Summary.StartedAt.ToLocalTime():ddd d MMM yyyy HH:mm} · {row.FilePath}";
        SessionMetaText.ToolTip = row.FilePath;
        ExportBtn.IsEnabled = OpenTranscriptBtn.IsEnabled = true;
        ShowEntryMessage("Reading transcript…");

        _reading = true;
        try
        {
            var (entries, next) = await Task.Run(() => SessionHistoryReader.ReadEntries(row.FilePath, row.Summary.Agent));
            if (generation != _sessionGeneration) return;

            _offset = next;
            _entries = new ObservableCollection<HistoryEntryRow>(entries.Select(e => new HistoryEntryRow(e, row.Summary.Agent)));
            _view = (ListCollectionView)CollectionViewSource.GetDefaultView(_entries);
            _view.Filter = Matches;
            EntryList.ItemsSource = _view;
            UpdateEntryCount();

            // Layout first, or the recycled panel keeps the previous session's offset.
            EntryList.UpdateLayout();
            if (follow && _view.Count > 0)
            {
                EntryScroller()?.ScrollToEnd();
                _atBottom = true;
            }
            else EntryScroller()?.ScrollToTop();
        }
        finally
        {
            if (generation == _sessionGeneration) _reading = false;
        }
    }

    // Appends whatever the CLI has written since the last read; every few ticks also picks up new sessions.
    private async Task LiveTickAsync()
    {
        if (++_ticks % 8 == 0 && LocationList.SelectedItem is LaunchLocation location)
            await LoadSessionsAsync(location, quiet: true);

        if (_session is not { } session || _reading) return;
        long length;
        try { length = new FileInfo(session.FilePath).Length; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
        if (length == _offset) return;

        var generation = _sessionGeneration;
        var offset = _offset;
        _reading = true;
        try
        {
            var (entries, next) = await Task.Run(() => SessionHistoryReader.ReadEntries(session.FilePath, session.Summary.Agent, offset));
            if (generation != _sessionGeneration) return;

            var follow = _atBottom;
            _offset = next;
            foreach (var entry in entries) _entries.Add(new HistoryEntryRow(entry, session.Summary.Agent));
            UpdateEntryCount();
            if (follow && entries.Count > 0) EntryScroller()?.ScrollToEnd();
        }
        finally
        {
            if (generation == _sessionGeneration) _reading = false;
        }
    }

    private bool Matches(object item)
    {
        if (item is not HistoryEntryRow row) return false;
        var shown = row.Entry.Kind switch
        {
            HistoryKind.Prompt => ShowPrompts.IsChecked == true,
            HistoryKind.Reply => ShowReplies.IsChecked == true,
            HistoryKind.Thinking => ShowThinking.IsChecked == true,
            HistoryKind.ToolCall => ShowTools.IsChecked == true,
            HistoryKind.ToolResult => ShowResults.IsChecked == true,
            _ => ShowEvents.IsChecked == true
        };
        if (!shown) return false;

        var search = SearchBox.Text.Trim();
        return search.Length == 0 ||
               row.Entry.Text.Contains(search, StringComparison.OrdinalIgnoreCase) ||
               row.Label.Contains(search, StringComparison.OrdinalIgnoreCase);
    }

    private void Filter_Click(object sender, RoutedEventArgs e) => ApplyFilter();

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        if (_view is null) return;
        _view.Refresh();
        UpdateEntryCount();
    }

    private void UpdateEntryCount()
    {
        if (_view is null) return;
        var prompts = _entries.Count(e => e.Entry.Kind == HistoryKind.Prompt);
        EntryCountText.Text = _view.Count == _entries.Count
            ? $"{_entries.Count:N0} entries · {prompts:N0} prompts"
            : $"{_view.Count:N0} of {_entries.Count:N0} entries";

        if (_entries.Count == 0) ShowEntryMessage("Nothing in this session yet. New messages appear here as the agent writes them.");
        else if (_view.Count == 0) ShowEntryMessage("No entries match the current filters.");
        else EntryEmptyText.Visibility = Visibility.Collapsed;
    }

    private void ShowEntryMessage(string message)
    {
        EntryEmptyText.Text = message;
        EntryEmptyText.Visibility = Visibility.Visible;
    }

    private void Expand_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is HistoryEntryRow row) row.IsExpanded = !row.IsExpanded;
    }

    // ---- scrolling ----

    private ScrollViewer? EntryScroller() => FindChild<ScrollViewer>(EntryList);

    private void EntryList_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.OriginalSource is not ScrollViewer sv || sv != EntryScroller()) return;
        _atBottom = sv.VerticalOffset >= sv.ScrollableHeight - 4;
        JumpLatestBtn.Visibility = _atBottom || _view is null || _view.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    // Each message body is a TextBox (so text can be selected), and its inner ScrollViewer would otherwise swallow the wheel.
    private void EntryList_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (EntryScroller() is not { } sv) return;
        sv.ScrollToVerticalOffset(sv.VerticalOffset - e.Delta);
        e.Handled = true;
    }

    private void JumpLatest_Click(object sender, RoutedEventArgs e)
    {
        EntryScroller()?.ScrollToEnd();
        _atBottom = true;
    }

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;
            if (FindChild<T>(child) is { } nested) return nested;
        }
        return null;
    }

    // ---- actions ----

    private void OpenTranscript_Click(object sender, RoutedEventArgs e)
    {
        if (_session is null) return;
        Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { "/select," + _session.FilePath } });
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_session is null || _view is null) return;
        var location = LocationList.SelectedItem as LaunchLocation;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export session history",
            Filter = "Markdown (*.md)|*.md",
            FileName = SafeFileName($"{location?.Repository?.Name ?? "session"} {_session.Summary.StartedAt.ToLocalTime():yyyy-MM-dd HHmm} {_session.Title}") + ".md"
        };
        if (dialog.ShowDialog(this) != true) return;

        var md = new StringBuilder();
        md.AppendLine($"# {_session.Title}").AppendLine();
        md.AppendLine($"- Agent: {_session.AgentName}");
        md.AppendLine($"- Folder: {location?.Path}");
        md.AppendLine($"- Started: {_session.Summary.StartedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
        md.AppendLine($"- Transcript: `{_session.FilePath}`");
        if (_view.Count != _entries.Count) md.AppendLine($"- Filtered: {_view.Count} of {_entries.Count} entries");
        md.AppendLine();

        foreach (HistoryEntryRow row in _view)
        {
            md.AppendLine($"## {row.Label}{(row.Entry.Timestamp is { } ts ? $" · {ts.ToLocalTime():HH:mm:ss}" : "")}").AppendLine();
            if (row.Entry.Kind is HistoryKind.ToolCall or HistoryKind.ToolResult)
            {
                var fence = row.Entry.Text.Contains("```") ? "````" : "```";
                md.AppendLine(fence).AppendLine(row.Entry.Text).AppendLine(fence);
            }
            else md.AppendLine(row.Entry.Text);
            md.AppendLine();
        }

        try
        {
            File.WriteAllText(dialog.FileName, md.ToString(), new UTF8Encoding(false));
            SessionMetaText.Text = $"Exported to {dialog.FileName}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "Export failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(c => invalid.Contains(c) ? '-' : c).ToArray()).Trim();
        return clean.Length > 120 ? clean[..120].Trim() : clean;
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
}

/// <summary>A session in the middle list.</summary>
public sealed class SessionRow(SessionSummary summary)
{
    private static readonly SolidColorBrush ClaudeBadge = Frozen(0x9A, 0x4B, 0x2A);
    private static readonly SolidColorBrush CodexBadge = Frozen(0x0F, 0x5E, 0x4A);

    public SessionSummary Summary { get; } = summary;
    public string FilePath => Summary.FilePath;

    public string Title => FirstLine(Summary.Title) ?? FirstLine(Summary.FirstPrompt) ?? "Untitled session";

    public string AgentName => Summary.Agent == ContextSource.Codex ? "Codex" : "Claude Code";
    public SolidColorBrush AgentBadgeBrush => Summary.Agent == ContextSource.Codex ? CodexBadge : ClaudeBadge;

    public Visibility LiveVisibility =>
        DateTimeOffset.UtcNow - Summary.LastActivity < TimeSpan.FromMinutes(2) ? Visibility.Visible : Visibility.Collapsed;

    public string Meta
    {
        get
        {
            var start = Summary.StartedAt.ToLocalTime();
            var when = start.Date == DateTime.Today ? $"Today {start:HH:mm}"
                : start.Date == DateTime.Today.AddDays(-1) ? $"Yesterday {start:HH:mm}"
                : start.ToString("d MMM yyyy HH:mm", CultureInfo.CurrentCulture);
            var span = Summary.LastActivity - Summary.StartedAt;
            var duration = span.TotalMinutes < 1 ? "<1m"
                : span.TotalHours < 1 ? $"{(int)span.TotalMinutes}m"
                : $"{(int)span.TotalHours}h {span.Minutes}m";
            return $"{when} · {duration} · {FormatSize(Summary.SizeBytes)}";
        }
    }

    // Screen readers and UI Automation name list items by ToString.
    public override string ToString() => $"{Title}, {AgentName}, {Meta}";

    private static string? FirstLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var line = text.Trim().Split('\n')[0].Trim();
        return line.Length > 160 ? line[..160] + "…" : line;
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1024 * 1024 => $"{bytes / (1024d * 1024):0.#} MB",
        >= 1024 => $"{bytes / 1024d:0} KB",
        _ => $"{bytes} B"
    };

    internal static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}

/// <summary>One entry in the timeline. Long tool output and thinking start collapsed to a preview.</summary>
public sealed class HistoryEntryRow : INotifyPropertyChanged
{
    private const int PreviewLines = 12;
    private const int PreviewChars = 1200;

    private static readonly SolidColorBrush PromptAccent = SessionRow.Frozen(0x2F, 0xD4, 0xF5);
    private static readonly SolidColorBrush ReplyAccent = SessionRow.Frozen(0x34, 0xE5, 0xA8);
    private static readonly SolidColorBrush ThinkingAccent = SessionRow.Frozen(0x8B, 0x7C, 0xF6);
    private static readonly SolidColorBrush ToolAccent = SessionRow.Frozen(0xFF, 0x7A, 0x45);
    private static readonly SolidColorBrush ResultAccent = SessionRow.Frozen(0x5A, 0x6B, 0x85);
    private static readonly SolidColorBrush ErrorAccent = SessionRow.Frozen(0xEF, 0x44, 0x44);
    private static readonly SolidColorBrush EventAccent = SessionRow.Frozen(0xF5, 0x9E, 0x0B);

    private static readonly SolidColorBrush PromptBg = SessionRow.Frozen(0x10, 0x26, 0x38);
    private static readonly SolidColorBrush ReplyBg = SessionRow.Frozen(0x12, 0x1A, 0x27);
    private static readonly SolidColorBrush QuietBg = SessionRow.Frozen(0x0E, 0x16, 0x22);
    private static readonly SolidColorBrush ResultBg = SessionRow.Frozen(0x0B, 0x12, 0x1C);

    private static readonly SolidColorBrush TextBrush = SessionRow.Frozen(0xE8, 0xF1, 0xFB);
    private static readonly SolidColorBrush SoftText = SessionRow.Frozen(0xB6, 0xC2, 0xD6);
    private static readonly SolidColorBrush MutedText = SessionRow.Frozen(0x9A, 0xA8, 0xBF);

    private static readonly FontFamily Ui = new("Segoe UI");
    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas");

    private readonly bool _collapsible;
    private readonly string _preview;
    private bool _expanded;

    public HistoryEntryRow(HistoryEntry entry, ContextSource agent)
    {
        Entry = entry;
        var subAgent = entry.IsSubAgent ? "SUB-AGENT · " : "";
        Label = subAgent + entry.Kind switch
        {
            HistoryKind.Prompt => "YOU",
            HistoryKind.Reply => agent == ContextSource.Codex ? "CODEX" : "CLAUDE",
            HistoryKind.Thinking => "THINKING",
            HistoryKind.ToolCall => $"TOOL · {entry.ToolName}",
            HistoryKind.ToolResult => (entry.IsError ? "ERROR" : "RESULT") + (entry.ToolName is null ? "" : $" · {entry.ToolName}"),
            _ => "EVENT"
        };
        (Accent, Background, BodyBrush) = entry.Kind switch
        {
            HistoryKind.Prompt => (PromptAccent, PromptBg, TextBrush),
            HistoryKind.Reply => (ReplyAccent, ReplyBg, TextBrush),
            HistoryKind.Thinking => (ThinkingAccent, QuietBg, MutedText),
            HistoryKind.ToolCall => (ToolAccent, QuietBg, SoftText),
            HistoryKind.ToolResult => (entry.IsError ? ErrorAccent : ResultAccent, ResultBg, entry.IsError ? SoftText : MutedText),
            _ => (EventAccent, QuietBg, SoftText)
        };
        var mono = entry.Kind is HistoryKind.ToolCall or HistoryKind.ToolResult;
        BodyFont = mono ? Mono : Ui;
        BodySize = mono ? 12 : 13;
        BodyStyle = entry.Kind == HistoryKind.Thinking ? FontStyles.Italic : FontStyles.Normal;

        _collapsible = entry.Kind is HistoryKind.Thinking or HistoryKind.ToolCall or HistoryKind.ToolResult or HistoryKind.Event;
        _preview = entry.Text;
        if (_collapsible)
        {
            var lines = entry.Text.Split('\n');
            if (lines.Length > PreviewLines || entry.Text.Length > PreviewChars)
            {
                var head = string.Join("\n", lines.Take(PreviewLines));
                _preview = head.Length > PreviewChars ? head[..PreviewChars] : head;
                LineCount = lines.Length;
            }
            else _collapsible = false;
        }

        if (entry.Timestamp is { } ts)
        {
            var local = ts.ToLocalTime();
            TimeText = local.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            TimeToolTip = local.ToString("dddd d MMMM yyyy, HH:mm:ss", CultureInfo.CurrentCulture);
        }
    }

    public HistoryEntry Entry { get; }
    public string Label { get; }
    public SolidColorBrush Accent { get; }
    public SolidColorBrush Background { get; }
    public SolidColorBrush BodyBrush { get; }
    public FontFamily BodyFont { get; }
    public double BodySize { get; }
    public FontStyle BodyStyle { get; }
    public string? TimeText { get; }
    public string? TimeToolTip { get; }
    private int LineCount { get; }

    public string Body => _expanded || !_collapsible ? Entry.Text : _preview;

    public Visibility ExpandVisibility => _collapsible ? Visibility.Visible : Visibility.Collapsed;

    public string ExpandText => _expanded
        ? "Show less"
        : LineCount > PreviewLines ? $"Show all {LineCount:N0} lines" : $"Show all {Entry.Text.Length:N0} characters";

    public bool IsExpanded
    {
        get => _expanded;
        set
        {
            if (_expanded == value) return;
            _expanded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Body)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ExpandText)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public override string ToString() => Label;
}
