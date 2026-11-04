using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AgentHub.Controls;
using AgentHub.Services;
using AgentHub.Services.Context;
using AgentHub.Services.History;
using AgentHub.Services.Timeline;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using Cursors = System.Windows.Input.Cursors;
using FontFamily = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;

namespace AgentHub;

/// <summary>
/// The story of one repository from its first commit or session to now: every session and every run of commits made
/// outside sessions, top to bottom in time, drawn as a tree in which a session overlapping another gets a lane of its
/// own. Built from the CLIs' transcripts and <c>git log</c> only; re-read every 20 seconds while open.
/// </summary>
public partial class TimelineWindow : Window
{
    private static readonly Brush CardBack = Frozen("#E6121A27"), CardBorder = Frozen("#1E2C40"), CardHover = Frozen("#24344B");
    private static readonly Brush Muted = Frozen("#7E8CA6"), Dim = Frozen("#5A6B85"), Body = Frozen("#B6C2D6"), Text = Frozen("#E8F1FB");
    private static readonly Brush ClaudeBadge = Frozen("#9A4B2A"), CodexBadge = Frozen("#0F5E4A");
    private static readonly Brush LiveBack = Frozen("#14532D"), LiveFore = Frozen("#86EFAC");
    private static readonly Brush ChipBack = Frozen("#141D2B"), PromptBack = Frozen("#0E1622");
    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas");

    private readonly ProcessRunner _runner;
    private readonly TimelineScope _scope;
    private readonly string? _archiveRoot;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(20) };
    private readonly HashSet<string> _expanded = new(StringComparer.OrdinalIgnoreCase);
    // Prompts already read from a transcript, reused while its length is unchanged (a transcript can be tens of MB).
    private readonly Dictionary<string, (long Length, List<HistoryEntry> Prompts)> _prompts = new(StringComparer.OrdinalIgnoreCase);
    private int _generation;
    private bool _building;
    private string? _signature;

    /// <summary>Raised by a session card's "Open in History": the folder the session ran in and its transcript.</summary>
    public event Action<string, string>? HistoryRequested;

    public TimelineWindow(ProcessRunner runner, TimelineScope scope, string? archiveRoot)
    {
        InitializeComponent();
        _runner = runner;
        _scope = scope;
        _archiveRoot = archiveRoot;
        Title = $"AgentHub - Timeline - {scope.Name}";
        HeadingText.Text = $"Timeline · {scope.Name}";
        SubheadingText.Text = scope.Root;
        SubheadingText.ToolTip = scope.IsGit && scope.Folders.Count > 1
            ? string.Join("\n", scope.Folders)
            : scope.Root;
        StatusText.Text = "Reading commits and transcripts…";

        Loaded += async (_, _) => await RefreshAsync(first: true);
        _timer.Tick += async (_, _) => await RefreshAsync(first: false);
        _timer.Start();
        Closed += (_, _) => _timer.Stop();
    }

    /// <summary>The repository's main folder (or the plain folder); one window per root.</summary>
    public string Root => _scope.Root;

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync(first: false, forced: true);

    private async Task RefreshAsync(bool first, bool forced = false)
    {
        if (_building) return;
        _building = true;
        var generation = ++_generation;
        RefreshBtn.IsEnabled = false;
        if (first || forced) StatusText.Text = "Reading commits and transcripts…";
        try
        {
            var commits = await TimelineSource.ReadCommitsAsync(_runner, _scope);
            var tips = await TimelineSource.ReadBranchTipsAsync(_runner, _scope);
            var model = await Task.Run(() =>
            {
                var sessions = TimelineSource.ReadSessions(_scope, _archiveRoot);
                return TimelineBuilder.Build(sessions, commits, tips, DateTimeOffset.UtcNow);
            });
            if (generation != _generation) return;

            var signature = Signature(model);
            if (forced || signature != _signature)
            {
                _signature = signature;
                Render(model, scrollToEnd: first);
            }
            StatusText.Text = model.Rows.Count == 0
                ? ""
                : $"Read at {DateTime.Now:HH:mm:ss} from {(_scope.IsGit ? "git log and " : "")}the transcripts of {model.SessionCount} session{(model.SessionCount == 1 ? "" : "s")}. " +
                  "A commit belongs to the session running when it was made; with several, to the one whose transcript names its hash.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            StatusText.Text = "Could not read the timeline: " + ex.Message;
        }
        finally
        {
            if (generation == _generation)
            {
                _building = false;
                RefreshBtn.IsEnabled = true;
            }
        }
    }

    // Everything the drawing depends on; an unchanged signature skips the rebuild, so the window does not flicker every 20 s.
    private static string Signature(TimelineModel model)
    {
        var sb = new StringBuilder();
        sb.Append(model.Columns).Append('|');
        foreach (var row in model.Rows)
        {
            sb.Append(row.Key).Append(':').Append(row.Column).Append(':').Append(row.IsLive ? 'L' : '-').Append(':').Append(row.Commits.Count).Append(':');
            if (row.Session is { } s) sb.Append(s.EndedAt.ToUnixTimeSeconds()).Append(':').Append(s.Summary.SizeBytes);
            foreach (var l in row.Lines) sb.Append(l.Column).Append(l.FromTop ? 't' : '-').Append(l.ToBottom ? 'b' : '-').Append(l.Fork ? 'f' : '-').Append(l.MergeAtBottom ? 'm' : '-');
            sb.Append('\n');
        }
        return sb.ToString();
    }

    // ---- rendering ----

    private const double SessionsShare = 1.15, CommitsShare = 1;

    private void Render(TimelineModel model, bool scrollToEnd)
    {
        var wasAtBottom = Scroller.VerticalOffset >= Scroller.ScrollableHeight - 2;
        var offset = Scroller.VerticalOffset;

        var since = model.Rows.Count > 0 ? model.Rows[0].Time.ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture) : null;
        var parts = new List<string> { $"{model.SessionCount} session{(model.SessionCount == 1 ? "" : "s")}" };
        if (_scope.IsGit) parts.Add($"{model.CommitCount} commit{(model.CommitCount == 1 ? "" : "s")}");
        if (since is not null) parts.Add("since " + since);
        if (_scope.Folders.Count > 1) parts.Add($"{_scope.Folders.Count} worktrees");
        SubheadingText.Text = string.Join(" · ", parts) + " · " + _scope.Root;

        // Three verticals: the tree, then what happened in sessions, then what landed in Git, each row at its time.
        _branchNames = model.Branches.ToDictionary(b => b.Key, b => b.Name, StringComparer.Ordinal);
        _trunkName = model.TrunkName;
        HeaderGutter.Width = TimelineGutter.GutterWidth(model.Columns);
        RowsPanel.Children.Clear();
        foreach (var row in model.Rows)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(SessionsShare, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(CommitsShare, GridUnitType.Star) });
            grid.Children.Add(new TimelineGutter { Row = row, Columns = model.Columns });

            var left = row.Kind == TimelineRowKind.Session ? SessionCard(row) : row.Kind == TimelineRowKind.Start ? StartCard(row) : null;
            if (left is not null)
            {
                Grid.SetColumn(left, 1);
                grid.Children.Add(left);
            }
            if (row.Commits.Count > 0)
            {
                var right = CommitsCard(row);
                Grid.SetColumn(right, 2);
                grid.Children.Add(right);
            }
            RowsPanel.Children.Add(grid);
        }

        EmptyText.Visibility = model.Rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = _scope.IsGit
            ? $"No commits and no Claude Code or Codex sessions found for\n{_scope.Root}"
            : $"No Claude Code or Codex sessions recorded in\n{_scope.Root}\n(not a Git repository, so there are no commits to show)";

        Scroller.UpdateLayout();
        // Keep the column headings over the columns when the scrollbar takes width from the rows.
        var bar = Scroller.ComputedVerticalScrollBarVisibility == Visibility.Visible ? SystemParameters.VerticalScrollBarWidth : 0;
        HeaderRow.Margin = new Thickness(0, 0, 8 + bar, 8);
        if (scrollToEnd || wasAtBottom) Scroller.ScrollToEnd();
        else Scroller.ScrollToVerticalOffset(offset);
    }

    // ---- cards ----

    /// <summary>The pieces of a card that the three kinds fill in differently.</summary>
    private sealed class Card
    {
        public required Border Border { get; init; }
        public required DockPanel Header { get; init; }
        public required StackPanel Body { get; init; }
        public required StackPanel TitleStack { get; init; }
        public required WrapPanel TitleRow { get; init; }
        public required TextBlock Title { get; init; }
        public required TextBlock Meta { get; init; }
        public required TextBlock Right { get; init; }
        public required TextBlock Chevron { get; init; }
    }

    private static Card NewCard(bool expandable, double leftMargin)
    {
        var border = new Border
        {
            Background = CardBack,
            BorderBrush = CardBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(leftMargin, 0, 0, 10),
            Padding = new Thickness(12, 8, 12, 9),
            VerticalAlignment = VerticalAlignment.Top
        };
        var stack = new StackPanel();
        border.Child = stack;

        var header = new DockPanel { LastChildFill = true, Cursor = expandable ? Cursors.Hand : Cursors.Arrow, Background = Brushes.Transparent };
        var body = new StackPanel { Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
        stack.Children.Add(header);
        stack.Children.Add(body);

        // Every card keeps the chevron's width, so titles line up whether or not the card expands.
        var chevron = new TextBlock { Text = expandable ? "▸" : "", Foreground = Muted, FontSize = 12, Width = 14, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 2, 0) };
        DockPanel.SetDock(chevron, Dock.Left);
        header.Children.Add(chevron);

        var right = new TextBlock { Foreground = Dim, FontSize = 11, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(12, 2, 0, 0) };
        DockPanel.SetDock(right, Dock.Right);
        header.Children.Add(right);

        var titleStack = new StackPanel();
        header.Children.Add(titleStack);
        var titleRow = new WrapPanel();
        titleStack.Children.Add(titleRow);
        var title = new TextBlock { Foreground = Text, FontSize = 13, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 8, 0) };
        titleRow.Children.Add(title);
        var meta = new TextBlock { Foreground = Muted, FontSize = 11, Margin = new Thickness(0, 3, 0, 0), TextWrapping = TextWrapping.Wrap };
        titleStack.Children.Add(meta);

        return new Card { Border = border, Header = header, Body = body, TitleStack = titleStack, TitleRow = titleRow, Title = title, Meta = meta, Right = right, Chevron = chevron };
    }

    // Clicking the header shows or hides the body; the body is filled once, on first expansion. Expansion survives rebuilds by key.
    private void MakeExpandable(Card card, string key, Action<StackPanel> fill)
    {
        var filled = false;
        void SetExpanded(bool expanded)
        {
            card.Body.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            card.Chevron.Text = expanded ? "▾" : "▸";
            if (expanded) _expanded.Add(key); else _expanded.Remove(key);
            if (expanded && !filled)
            {
                filled = true;
                fill(card.Body);
            }
        }
        card.Header.MouseLeftButtonUp += (_, e) => { SetExpanded(card.Body.Visibility != Visibility.Visible); e.Handled = true; };
        card.Header.MouseEnter += (_, _) => card.Border.BorderBrush = CardHover;
        card.Header.MouseLeave += (_, _) => card.Border.BorderBrush = CardBorder;
        if (_expanded.Contains(key)) SetExpanded(true);
    }

    private static FrameworkElement StartCard(TimelineRow row)
    {
        var card = NewCard(expandable: false, leftMargin: 6);
        card.Title.Text = "Start";
        card.Right.Text = When(row.Time);
        card.Meta.Text = row.Commits.Count > 0 ? "The repository's first commit" : "The first session recorded here";
        return card.Border;
    }

    private FrameworkElement SessionCard(TimelineRow row)
    {
        var session = row.Session!;
        var summary = session.Summary;
        var card = NewCard(expandable: true, leftMargin: 6);
        card.Title.Text = FirstLine(summary.Title) ?? FirstLine(summary.FirstPrompt) ?? "Untitled session";
        card.Title.ToolTip = summary.FilePath;
        card.TitleRow.Children.Add(Badge(summary.Agent == ContextSource.Codex ? "Codex" : "Claude Code", summary.Agent == ContextSource.Codex ? CodexBadge : ClaudeBadge, Text));
        if (row.IsLive) card.TitleRow.Children.Add(Badge("LIVE", LiveBack, LiveFore, "Written to in the last three minutes"));
        if (summary.ArchivedOnly) card.TitleRow.Children.Add(Badge("archived", ChipBack, Muted, "The CLI has deleted its copy; read from AgentHub's archive"));
        card.Right.Text = Span(summary.StartedAt, session.EndedAt);

        var bits = new List<string> { Duration(session.EndedAt - summary.StartedAt) };
        bits.Add(row.Commits.Count == 0 ? "no commits" : $"{row.Commits.Count} commit{(row.Commits.Count == 1 ? "" : "s")} →");
        if (row.Branch is { } lane) bits.Add($"on {lane.Name}{(lane.MergeHash is null ? ", not merged" : "")}");
        else if (row.Commits.Count > 0) bits.Add($"on {_trunkName}");
        if (session.Folder is not null && _scope.Folders.Count > 1) bits.Add(Path.GetFileName(session.Folder.TrimEnd(Path.DirectorySeparatorChar)));
        card.Meta.Text = string.Join(" · ", bits);

        MakeExpandable(card, row.Key, body =>
        {
            var prompts = new StackPanel();
            body.Children.Add(Section("Prompts"));
            body.Children.Add(prompts);
            _ = LoadPromptsAsync(prompts, session);

            if (session.Folder is { } folder)
            {
                var open = new Button { Content = "🕘 Open in History", Style = (Style)FindResource("CardButton"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0), ToolTip = "Show the full transcript of this session: replies, thinking, tool calls and results" };
                open.Click += (_, _) => HistoryRequested?.Invoke(folder, summary.FilePath);
                body.Children.Add(open);
            }
        });
        return card.Border;
    }

    /// <summary>Commits in the right column, level with the session that made them: the first few always shown, the rest behind the chevron.</summary>
    private FrameworkElement CommitsCard(TimelineRow row)
    {
        const int shown = 3;
        var commits = row.Commits;
        var card = NewCard(expandable: commits.Count > shown, leftMargin: 10);
        card.Title.Text = row.Kind switch
        {
            TimelineRowKind.Start => "First commit",
            TimelineRowKind.Session => $"{commits.Count} commit{(commits.Count == 1 ? "" : "s")} in this session",
            _ => $"{commits.Count} commit{(commits.Count == 1 ? "" : "s")} outside sessions"
        };
        card.Right.Text = commits.Count > 1 ? Span(commits[0].Time, commits[^1].Time) : When(commits[0].Time);
        var authors = commits.Select(c => c.Author).Distinct().ToList();
        var branches = commits.Select(BranchName).Where(b => b is not null).Distinct().ToList();
        var bits = new List<string> { authors.Count == 1 ? authors[0] : $"{authors.Count} authors" };
        if (branches.Count > 0) bits.Add(string.Join(", ", branches));
        if (row.Branch is { MergeHash: null } open && row.Kind != TimelineRowKind.Session) bits.Add("not merged");
        if (commits.Count > shown) bits.Add($"{commits.Count - shown} more below");
        card.Meta.Text = string.Join(" · ", bits);
        card.Meta.Margin = new Thickness(0, 3, 0, 6);

        // The first commits sit under the title, so they are read without expanding.
        foreach (var commit in commits.Take(shown)) card.TitleStack.Children.Add(CommitLine(commit, branches.Count > 1));
        if (commits.Count > shown)
        {
            card.Body.Margin = new Thickness(16, 0, 0, 0);
            MakeExpandable(card, row.Key + ":commits", body =>
            {
                foreach (var commit in commits.Skip(shown)) body.Children.Add(CommitLine(commit, branches.Count > 1));
            });
        }
        return card.Border;
    }

    private async Task LoadPromptsAsync(StackPanel target, TimelineSessionInput session)
    {
        var path = session.Summary.FilePath;
        var reading = new TextBlock { Text = "Reading transcript…", Foreground = Dim, FontSize = 11, Margin = new Thickness(0, 2, 0, 4) };
        target.Children.Add(reading);

        List<HistoryEntry> prompts;
        try
        {
            long length;
            try { length = new FileInfo(path).Length; } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { length = -1; }
            if (_prompts.TryGetValue(path, out var cached) && cached.Length == length) prompts = cached.Prompts;
            else
            {
                prompts = await Task.Run(() => SessionHistoryReader.ReadEntries(path, session.Summary.Agent).Entries
                    .Where(e => e.Kind == HistoryKind.Prompt && !e.IsSubAgent).ToList());
                _prompts[path] = (length, prompts);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            reading.Text = "Could not read the transcript: " + ex.Message;
            return;
        }

        target.Children.Remove(reading);
        if (prompts.Count == 0)
        {
            target.Children.Add(new TextBlock { Text = "No prompts recorded.", Foreground = Dim, FontSize = 11, Margin = new Thickness(0, 2, 0, 4) });
            return;
        }
        foreach (var prompt in prompts)
        {
            var line = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
            var time = new TextBlock { Text = prompt.Timestamp is { } t ? t.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture) : "", Foreground = Dim, FontSize = 10, Width = 38, Margin = new Thickness(0, 4, 0, 0), VerticalAlignment = VerticalAlignment.Top };
            DockPanel.SetDock(time, Dock.Left);
            line.Children.Add(time);
            var text = Shorten(prompt.Text, 400);
            line.Children.Add(new Border
            {
                Background = PromptBack,
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(8, 4, 8, 4),
                Child = new TextBlock { Text = text, Foreground = Body, FontSize = 12, TextWrapping = TextWrapping.Wrap, ToolTip = text.Length < prompt.Text.Length ? Shorten(prompt.Text, 2000) : null }
            });
            target.Children.Add(line);
        }
    }

    /// <summary>Time, hash and subject on one wrapping line; the branch follows the subject only when the card spans several.</summary>
    // Branch names by the graph's lane keys, and the trunk's name, from the model last rendered.
    private static Dictionary<string, string> _branchNames = new(StringComparer.Ordinal);
    private static string _trunkName = "main";

    private static string? BranchName(TimelineCommit commit) =>
        commit.Branch is { } key && _branchNames.TryGetValue(key, out var name) ? name : null;

    private static FrameworkElement CommitLine(TimelineCommit commit, bool showBranch)
    {
        var line = new DockPanel { Margin = new Thickness(0, 0, 0, 3) };
        var time = new TextBlock { Text = commit.Time.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture), Foreground = Dim, FontSize = 10, Width = 38, Margin = new Thickness(0, 2, 0, 0), VerticalAlignment = VerticalAlignment.Top };
        DockPanel.SetDock(time, Dock.Left);
        line.Children.Add(time);
        var hash = new TextBlock { Text = commit.Short, Foreground = Muted, FontFamily = Mono, FontSize = 11, Margin = new Thickness(0, 1, 8, 0), VerticalAlignment = VerticalAlignment.Top, ToolTip = $"{commit.Hash}\n{commit.Author}, {commit.Time.ToLocalTime():ddd d MMM yyyy HH:mm}" };
        DockPanel.SetDock(hash, Dock.Left);
        line.Children.Add(hash);
        var subject = new TextBlock { Foreground = Body, FontSize = 12, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Top };
        subject.Inlines.Add(new Run((commit.IsMerge ? "⇅ " : "") + commit.Subject));
        if (showBranch && BranchName(commit) is { } branch) subject.Inlines.Add(new Run("  " + branch) { Foreground = Dim, FontSize = 10 });
        line.Children.Add(subject);
        return line;
    }

    private static TextBlock Section(string text) => new()
    {
        Text = text.ToUpperInvariant(),
        Foreground = Dim,
        FontSize = 10,
        FontWeight = FontWeights.Bold,
        Margin = new Thickness(0, 6, 0, 4)
    };

    private static Border Badge(string text, Brush back, Brush fore, string? tip = null) => new()
    {
        Background = back,
        CornerRadius = new CornerRadius(4),
        Padding = new Thickness(5, 1, 5, 1),
        Margin = new Thickness(0, 1, 6, 0),
        VerticalAlignment = VerticalAlignment.Center,
        ToolTip = tip,
        Child = new TextBlock { Text = text, Foreground = fore, FontSize = 10, FontWeight = FontWeights.SemiBold }
    };

    // ---- text helpers ----

    private static string When(DateTimeOffset time)
    {
        var local = time.ToLocalTime();
        if (local.Date == DateTime.Today) return $"Today {local:HH:mm}";
        if (local.Date == DateTime.Today.AddDays(-1)) return $"Yesterday {local:HH:mm}";
        return local.Year == DateTime.Today.Year
            ? local.ToString("ddd d MMM HH:mm", CultureInfo.CurrentCulture)
            : local.ToString("ddd d MMM yyyy HH:mm", CultureInfo.CurrentCulture);
    }

    private static string Span(DateTimeOffset from, DateTimeOffset to)
    {
        var a = from.ToLocalTime();
        var b = to.ToLocalTime();
        if (b <= a) return When(from);
        return a.Date == b.Date ? $"{When(from)} → {b:HH:mm}" : $"{When(from)} → {When(to)}";
    }

    private static string Duration(TimeSpan span) =>
        span.TotalMinutes < 1 ? "under a minute"
        : span.TotalHours < 1 ? $"{(int)span.TotalMinutes} min"
        : span.TotalDays < 1 ? $"{(int)span.TotalHours} h {span.Minutes} min"
        : $"{(int)span.TotalDays} d {span.Hours} h";

    private static string? FirstLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var line = text.Trim().Split('\n')[0].Trim();
        return line.Length > 160 ? line[..160] + "…" : line;
    }

    private static string Shorten(string text, int max)
    {
        var t = text.Trim();
        return t.Length > max ? t[..max] + "…" : t;
    }

    private static SolidColorBrush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
