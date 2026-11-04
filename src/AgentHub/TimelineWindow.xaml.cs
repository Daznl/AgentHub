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
using AgentHub.Services.Instructions;
using AgentHub.Services.Timeline;
using Brush = System.Windows.Media.Brush;
using MessageBox = System.Windows.MessageBox;
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
    // Lane highlighting: pointing at a lane lights its branch everywhere; a click keeps it lit until the next click elsewhere.
    private readonly List<TimelineGutter> _gutters = [];
    private readonly List<(Border Card, string? LaneKey)> _cards = [];
    private readonly Dictionary<string, TimelineBranch> _branches = new(StringComparer.Ordinal);
    private string? _hoverKey, _pinnedKey, _statusBase;
    // Session cards by row key, so a report's evidence can jump to the session it names.
    private readonly Dictionary<string, Border> _sessionCards = new(StringComparer.Ordinal);
    private int _generation;
    private bool _building;
    private string? _signature;
    private TimelineModel? _model;
    // Squash merges leave no trace in git's graph: the pull requests (read once, and again on Refresh) and a change
    // matcher say which trunk commit brought each such branch in, so its lane curves back like any other.
    private List<PullRequestFact>? _pullRequests;
    private bool _pullRequestsRead;
    private readonly SquashMatcher _squashes;

    /// <summary>Raised by a session card's "Open in History": the folder the session ran in and its transcript.</summary>
    public event Action<string, string>? HistoryRequested;

    /// <summary>Raised by the report's "Add to CLAUDE.md…": rules to show in the user-wide Context editor with Apply boxes.</summary>
    public event Action<ReviewSuggestion>? SuggestionsRequested;

    /// <summary>Command that starts Claude Code for the review (from the configured agent, else <c>claude</c>).</summary>
    public string ReviewCommand { get; set; } = "claude";

    public TimelineWindow(ProcessRunner runner, TimelineScope scope, string? archiveRoot)
    {
        InitializeComponent();
        Controls.DarkTitleBar.Apply(this);
        _runner = runner;
        _scope = scope;
        _archiveRoot = archiveRoot;
        _squashes = new SquashMatcher(runner, scope);
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
        Closed += (_, _) => { _timer.Stop(); _reviewCts?.Cancel(); };
        // A click that reaches the scroller hit no lane and no card header: it clears a pinned highlight, as does Escape.
        // Escape with nothing pinned closes the window, like the ✕ Close button.
        Scroller.MouseLeftButtonUp += (_, e) => { if (!IsInsideCard(e.OriginalSource as DependencyObject)) Pin(null); };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            if (_pinnedKey is not null) Pin(null);
            else Close();
            e.Handled = true;
        };
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

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
            if (_scope.IsGit && (!_pullRequestsRead || forced))
            {
                _pullRequests = await TimelineSource.ReadPullRequestsAsync(_runner, _scope);
                _pullRequestsRead = true;
            }
            var squashes = await _squashes.MatchAsync(commits, BranchGraph.OpenTips(commits, tips), SquashMatcher.FromPullRequests(_pullRequests, commits));
            var model = await Task.Run(() =>
            {
                var sessions = TimelineSource.ReadSessions(_scope, _archiveRoot);
                return TimelineBuilder.Build(sessions, commits, tips, DateTimeOffset.UtcNow, squashes);
            });
            if (generation != _generation) return;
            _model = model;
            UpdateReviewButtons();

            var signature = Signature(model);
            if (forced || signature != _signature)
            {
                _signature = signature;
                Render(model, scrollToEnd: first);
            }
            _statusBase = model.Rows.Count == 0
                ? ""
                : $"Read at {DateTime.Now:HH:mm:ss} from {(_scope.IsGit ? "git log and " : "")}the transcripts of {model.SessionCount} session{(model.SessionCount == 1 ? "" : "s")}. " +
                  "A commit belongs to the session running when it was made; with several, to the one whose transcript names its hash.";
            if (ActiveKey is null && _reviewCts is null) StatusText.Text = _statusBase;
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
        // Branches still open, and those that exist only here: what is waiting to be merged or pushed.
        var open = model.Branches.Count(b => !b.IsTrunk && !b.IsImported && b.MergeHash is null);
        var unpushed = model.Branches.Count(b => b.IsLocalOnly);
        if (open > 0) parts.Add($"{open} branch{(open == 1 ? "" : "es")} not merged");
        if (unpushed > 0) parts.Add($"{unpushed} not pushed");
        if (_scope.Folders.Count > 1) parts.Add($"{_scope.Folders.Count} worktrees");
        SubheadingText.Text = string.Join(" · ", parts) + " · " + _scope.Root;

        // Three verticals: the tree, then what happened in sessions, then what landed in Git, each row at its time.
        _branchNames = model.Branches.ToDictionary(b => b.Key, b => b.Name, StringComparer.Ordinal);
        _trunkName = model.TrunkName;
        HeaderGutter.Width = TimelineGutter.GutterWidth(model.Columns);
        _branches.Clear();
        foreach (var b in model.Branches) _branches[b.Key] = b;
        _gutters.Clear();
        _cards.Clear();
        _sessionCards.Clear();
        RowsPanel.Children.Clear();
        foreach (var row in model.Rows)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(SessionsShare, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(CommitsShare, GridUnitType.Star) });
            var gutter = new TimelineGutter { Row = row, Columns = model.Columns, HighlightKey = ActiveKey };
            gutter.LaneHovered += (_, key) => { _hoverKey = key; ApplyHighlight(); };
            gutter.LaneClicked += (_, key) => Pin(key);
            grid.Children.Add(gutter);
            _gutters.Add(gutter);

            var left = row.Kind == TimelineRowKind.Session ? SessionCard(row) : row.Kind == TimelineRowKind.Start ? StartCard(row) : null;
            if (left is not null)
            {
                Grid.SetColumn(left, 1);
                grid.Children.Add(left);
                _cards.Add((left, row.LaneKey));
                if (row.Kind == TimelineRowKind.Session) _sessionCards[row.Key] = left;
            }
            if (row.Commits.Count > 0)
            {
                var right = CommitsCard(row);
                Grid.SetColumn(right, 2);
                grid.Children.Add(right);
                _cards.Add((right, row.Branch?.Key ?? TimelineBranch.TrunkKey));
            }
            RowsPanel.Children.Add(grid);
        }
        if (_pinnedKey is not null && !_branches.ContainsKey(_pinnedKey)) _pinnedKey = null;
        ApplyHighlight();

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

    // ---- lane highlighting ----

    private string? ActiveKey => _pinnedKey ?? _hoverKey;

    private void Pin(string? key)
    {
        _pinnedKey = key == _pinnedKey ? null : key;
        ApplyHighlight();
    }

    // Lights the active branch: its lane bold in every gutter, its cards framed in the lane colour, everything else faint.
    private void ApplyHighlight()
    {
        var key = ActiveKey;
        foreach (var gutter in _gutters) gutter.HighlightKey = key;
        var frame = key is not null && _branches.TryGetValue(key, out var branch) ? TimelineGutter.LaneBrush(branch.ColourIndex) : null;
        foreach (var (card, laneKey) in _cards)
        {
            var lit = key is not null && laneKey == key;
            card.Tag = lit ? frame : null;
            card.BorderBrush = lit ? frame! : CardBorder;
            card.BorderThickness = new Thickness(lit ? 1.5 : 1);
            card.Opacity = key is null || lit ? 1 : 0.4;
        }

        if (_reviewCts is not null) { }
        else if (key is null) StatusText.Text = _statusBase ?? "";
        else if (_branches.TryGetValue(key, out var b))
        {
            var sessions = _cards.Count(c => c.LaneKey == key && c.Card.Tag is not null && Grid.GetColumn(c.Card) == 1);
            var commits = b.Commits.Count;
            StatusText.Text = $"{b.Name}: {sessions} session{(sessions == 1 ? "" : "s")}, {commits} commit{(commits == 1 ? "" : "s")}" +
                              (b.IsTrunk ? "" : b.IsSquash ? ", squash-merged" : b.MergesBack ? ", merged" : ", not merged") + (b.IsLocalOnly ? ", not pushed" : "") +
                              (_pinnedKey is null ? " · click the line to keep it lit" : " · click empty space or press Esc to clear");
        }
    }

    private static bool IsInsideCard(DependencyObject? source)
    {
        for (var d = source; d is not null; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is Border { Parent: Grid } border && Grid.GetColumn(border) > 0) return true;
        return false;
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
        card.Header.MouseLeave += (_, _) => card.Border.BorderBrush = card.Border.Tag as Brush ?? CardBorder;
        if (_expanded.Contains(key)) SetExpanded(true);
    }

    private static Border StartCard(TimelineRow row)
    {
        var card = NewCard(expandable: false, leftMargin: 6);
        card.Title.Text = "Start";
        card.Right.Text = When(row.Time);
        card.Meta.Text = row.Branch is { IsImported: true } ? "The first commit of history brought in from another repository"
            : row.Commits.Count > 0 ? "The repository's first commit" : "The first session recorded here";
        return card.Border;
    }

    private Border SessionCard(TimelineRow row)
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
        if (row.Branch is { } lane) bits.Add($"on {lane.Name}{(lane.MergeHash is null ? ", not merged" : "")}{(lane.IsLocalOnly ? ", not pushed" : "")}");
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
    private Border CommitsCard(TimelineRow row)
    {
        const int shown = 3;
        var commits = row.Commits;
        var card = NewCard(expandable: commits.Count > shown, leftMargin: 10);
        card.Title.Text = row.Kind switch
        {
            TimelineRowKind.Start => "First commit",
            TimelineRowKind.Session => $"{commits.Count} commit{(commits.Count == 1 ? "" : "s")} in this session",
            _ when row.Branch is { IsImported: true } => $"{commits.Count} commit{(commits.Count == 1 ? "" : "s")} imported from another repository",
            _ => $"{commits.Count} commit{(commits.Count == 1 ? "" : "s")} outside sessions"
        };
        card.Right.Text = commits.Count > 1 ? Span(commits[0].Time, commits[^1].Time) : When(commits[0].Time);
        var authors = commits.Select(c => c.Author).Distinct().ToList();
        var branches = commits.Select(BranchName).Where(b => b is not null).Distinct().ToList();
        var bits = new List<string> { authors.Count == 1 ? authors[0] : $"{authors.Count} authors" };
        if (branches.Count > 0) bits.Add(string.Join(", ", branches));
        if (row.Branch is { IsImported: true }) bits.Add("made elsewhere, before this history was brought in; its sessions are not in this timeline");
        else if (row.Branch is { MergeHash: null } && row.Kind != TimelineRowKind.Session) bits.Add("not merged");
        if (row.Branch is { IsLocalOnly: true } && row.Kind != TimelineRowKind.Session) bits.Add("not pushed");
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

        IReadOnlyList<HistoryEntry> prompts;
        try
        {
            // One shared cache with the review, so an expanded card and a review read a transcript once between them.
            prompts = (await Task.Run(() => TranscriptTexts.Read(path, session.Summary.Agent))).Prompts;
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

    // ---- review: Claude Code reads a digest of the timeline and says what to do differently ----

    private static readonly Brush ScoreGood = Frozen("#86EFAC"), ScoreMid = Frozen("#FCD34D"), ScorePoor = Frozen("#FCA5A5"), Accent = Frozen("#2FD4F5");
    private static readonly Brush EvidenceBack = Frozen("#0E1622"), ReportCardBack = Frozen("#E6121A27");

    /// <summary>One review's button, panel and state; there is one per <see cref="ReviewKind"/>.</summary>
    private sealed class ReviewSlot
    {
        public required ReviewKind Kind { get; init; }
        public required Button Button { get; init; }
        public required string Label { get; init; }
        public required Border Panel { get; init; }
        public required StackPanel Body { get; init; }
        public required TextBlock Meta { get; init; }
        public required Button AddRules { get; init; }
        public CancellationTokenSource? Cts { get; set; }
        public TimelineReviewResult? Result { get; set; }
    }

    private ReviewSlot[] _slots = [];
    private CancellationTokenSource? _reviewCts; // the one review running; only one at a time, so the status line is unambiguous

    private ReviewSlot Slot(ReviewKind kind)
    {
        if (_slots.Length == 0)
            _slots =
            [
                new ReviewSlot { Kind = ReviewKind.Interaction, Button = ReviewSessionsBtn, Label = "✨ You & Claude", Panel = SessionsReportPanel, Body = SessionsReportBody, Meta = SessionsReportMeta, AddRules = SessionsAddRulesBtn },
                new ReviewSlot { Kind = ReviewKind.GitHub, Button = ReviewGitHubBtn, Label = "✨ GitHub cycle", Panel = GitHubReportPanel, Body = GitHubReportBody, Meta = GitHubReportMeta, AddRules = GitHubAddRulesBtn },
            ];
        return _slots.First(s => s.Kind == kind);
    }

    private void UpdateReviewButtons()
    {
        var canStart = _reviewCts is null && _model is { Rows.Count: > 0 };
        foreach (var kind in new[] { ReviewKind.Interaction, ReviewKind.GitHub })
        {
            var slot = Slot(kind);
            slot.Button.IsEnabled = canStart || slot.Cts is not null;
            slot.Button.Content = slot.Cts is not null ? "■ Cancel" : slot.Label;
        }
    }

    private async void ReviewSessions_Click(object sender, RoutedEventArgs e) => await RunReviewAsync(ReviewKind.Interaction);
    private async void ReviewGitHub_Click(object sender, RoutedEventArgs e) => await RunReviewAsync(ReviewKind.GitHub);

    private async Task RunReviewAsync(ReviewKind kind)
    {
        var slot = Slot(kind);
        if (slot.Cts is { } running)
        {
            running.Cancel();
            return;
        }
        if (_reviewCts is not null || _model is not { } model) return;

        var cts = new CancellationTokenSource();
        slot.Cts = cts;
        _reviewCts = cts;
        UpdateReviewButtons();
        var title = TimelineReview.Title(kind);
        var started = DateTime.Now;
        var progress = new ReviewProgress("Starting…");
        var claudeFrom = (DateTime?)null;
        // Elapsed time always; once the digest exists, its size and an estimate from earlier reviews, then how far past it we are.
        void Tick()
        {
            var elapsed = (int)(DateTime.Now - started).TotalSeconds;
            if (progress.Estimate is not { } estimate || claudeFrom is not { } from)
            {
                StatusText.Text = $"{title} review: {progress.Stage} {elapsed} s. Reading the transcripts takes seconds.";
                return;
            }
            var inClaude = (int)(DateTime.Now - from).TotalSeconds;
            var size = $"{progress.DigestChars / 1024.0:0} KB digest";
            var about = ReviewTimings.Describe(estimate);
            StatusText.Text = inClaude <= estimate.TotalSeconds
                ? $"{title} review: {progress.Stage} {inClaude} s of {about} for a {size} (estimate from {progress.EstimateBasis}) · {elapsed} s in all"
                : $"{title} review: {progress.Stage} {inClaude} s, past the {about} estimated for a {size}; still running · {elapsed} s in all";
        }
        var ticker = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        ticker.Tick += (_, _) => Tick();
        Tick();
        ticker.Start();
        var reporter = new Progress<ReviewProgress>(p => { progress = p; if (p.Estimate is not null) claudeFrom ??= DateTime.Now; Tick(); });
        try
        {
            var result = await TimelineReview.RunAsync(_runner, ReviewCommand, _scope, model, kind, reporter, cts.Token);
            slot.Result = result;
            ShowReport(slot, result);
            var estimate = progress.Estimate is { } est ? $" (estimated {ReviewTimings.Describe(est)})" : "";
            StatusText.Text = $"{title} review done in {(int)(DateTime.Now - started).TotalSeconds} s, {(int)result.ClaudeTime.TotalSeconds} s of it Claude Code on a {progress.DigestChars / 1024.0:0} KB digest{estimate}. Report saved to {result.ReportPath}";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = $"{title} review cancelled.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            StatusText.Text = $"{title} review failed.";
            MessageBox.Show(this, ex.Message, "AgentHub - Timeline review", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            ticker.Stop();
            slot.Cts = null;
            _reviewCts = null;
            cts.Dispose();
            UpdateReviewButtons();
        }
    }

    private void ShowReport(ReviewSlot slot, TimelineReviewResult result)
    {
        var report = result.Report;
        slot.Body.Children.Clear();
        var checks = report.Facts.Checks.Count(c => c.Kind == report.Kind);
        slot.Meta.Text = $"{_scope.Name} · {DateTime.Now:ddd d MMM HH:mm} · Claude Code read {checks} checks, " +
                         (report.Kind == ReviewKind.Interaction ? "the sessions with your prompts, and your CLAUDE.md" : "the branches, pull requests and sessions, and your CLAUDE.md");

        if (report.Summary.Length > 0)
            slot.Body.Children.Add(new TextBlock { Text = report.Summary, Foreground = Body, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });

        slot.Body.Children.Add(PartCard(report.Kind, report.Part, result));

        var adds = ReportCard();
        var addsStack = (StackPanel)adds.Child;
        addsStack.Children.Add(new TextBlock { Text = "RULES TO ADD TO YOUR CLAUDE.md", Foreground = Dim, FontSize = 10, FontWeight = FontWeights.Bold });
        if (report.Additions.Count == 0)
            addsStack.Children.Add(new TextBlock { Text = "None suggested: your file already covers what went wrong, or nothing did.", Foreground = Muted, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });
        foreach (var a in report.Additions)
        {
            addsStack.Children.Add(new TextBlock { Text = a.Title, Foreground = Text, FontSize = 12, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 2) });
            if (a.Why.Length > 0) addsStack.Children.Add(new TextBlock { Text = a.Why, Foreground = Muted, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 3) });
            addsStack.Children.Add(new Border
            {
                Background = EvidenceBack, CornerRadius = new CornerRadius(5), Padding = new Thickness(8, 4, 8, 4),
                Child = new TextBlock { Text = a.Markdown.Trim(), Foreground = Body, FontFamily = Mono, FontSize = 11, TextWrapping = TextWrapping.Wrap }
            });
        }
        slot.Body.Children.Add(adds);
        slot.AddRules.IsEnabled = report.Additions.Count > 0;

        slot.Panel.Visibility = Visibility.Visible;
        ReportsColumn.Visibility = Visibility.Visible;
    }

    // The column beside the tree only takes its width while a report is open.
    private void UpdateReportsColumn() =>
        ReportsColumn.Visibility = _slots.Any(s => s.Panel.Visibility == Visibility.Visible) ? Visibility.Visible : Visibility.Collapsed;

    private static Border ReportCard() => new()
    {
        Background = ReportCardBack, BorderBrush = CardBorder, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
        Padding = new Thickness(12, 9, 12, 10), Margin = new Thickness(0, 0, 0, 10), Child = new StackPanel()
    };

    private Border PartCard(ReviewKind kind, ReviewPart part, TimelineReviewResult result)
    {
        var card = ReportCard();
        var stack = (StackPanel)card.Child;

        var head = new DockPanel { LastChildFill = true };
        var score = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(12, 0, 0, 0) };
        DockPanel.SetDock(score, Dock.Right);
        var scoreBrush = part.Score is { } s ? s >= 8 ? ScoreGood : s >= 5 ? ScoreMid : ScorePoor : Dim;
        score.Children.Add(new TextBlock { Text = part.Score is { } sc ? $"{sc}/10" : "–", Foreground = scoreBrush, FontSize = 22, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Right });
        score.Children.Add(new TextBlock { Text = part.ScoreSource, Foreground = Dim, FontSize = 10, HorizontalAlignment = HorizontalAlignment.Right, ToolTip = kind == ReviewKind.Interaction
            ? "Claude Code's judgement of how the sessions were run; an inference from the digest, not a measurement"
            : "Computed by AgentHub from its checks over git and GitHub (share of items passing each applicable check, averaged); the findings are Claude Code's" });
        head.Children.Add(score);
        var titles = new StackPanel();
        titles.Children.Add(new TextBlock { Text = TimelineReview.Title(kind).ToUpperInvariant(), Foreground = Dim, FontSize = 10, FontWeight = FontWeights.Bold });
        if (part.Verdict.Length > 0) titles.Children.Add(new TextBlock { Text = part.Verdict, Foreground = Text, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
        head.Children.Add(titles);
        stack.Children.Add(head);

        if (part.Findings.Count == 0)
            stack.Children.Add(new TextBlock { Text = "No findings.", Foreground = Muted, FontSize = 11, Margin = new Thickness(0, 8, 0, 0) });
        var n = 1;
        foreach (var f in part.Findings)
        {
            var finding = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
            var line = new WrapPanel();
            line.Children.Add(new TextBlock { Text = $"{n++}. {f.What}", Foreground = Text, FontSize = 12, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, MaxWidth = 330, Margin = new Thickness(0, 0, 8, 0) });
            line.Children.Add(Badge(TimelineReview.Who(f.Who), f.Who == "claude" ? ClaudeBadge : f.Who == "you" ? CodexBadge : ChipBack, Text));
            finding.Children.Add(line);
            if (f.Evidence.Length > 0) finding.Children.Add(EvidenceLine(f.Evidence, result));
            if (f.Instead.Length > 0)
            {
                var instead = new TextBlock { Foreground = Body, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) };
                instead.Inlines.Add(new Run("Instead: ") { Foreground = Muted });
                instead.Inlines.Add(new Run(f.Instead));
                finding.Children.Add(instead);
            }
            stack.Children.Add(finding);
        }
        return card;
    }

    // Evidence in mono; a session id it names (S12) is a link that scrolls to that session's card and flashes it.
    private TextBlock EvidenceLine(string evidence, TimelineReviewResult result)
    {
        var block = new TextBlock { Foreground = Muted, FontFamily = Mono, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) };
        var at = 0;
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(evidence, @"\bS\d+\b"))
        {
            if (m.Index > at) block.Inlines.Add(new Run(evidence[at..m.Index]));
            if (result.Digest.SessionRowKeys.TryGetValue(m.Value, out var key) && _sessionCards.ContainsKey(key))
            {
                var link = new Run(m.Value) { Foreground = Accent, TextDecorations = TextDecorations.Underline, Cursor = Cursors.Hand, ToolTip = "Show this session in the timeline" };
                link.MouseLeftButtonUp += (_, e) => { JumpToSession(key); e.Handled = true; };
                block.Inlines.Add(link);
            }
            else block.Inlines.Add(new Run(m.Value));
            at = m.Index + m.Length;
        }
        if (at < evidence.Length) block.Inlines.Add(new Run(evidence[at..]));
        return block;
    }

    private void JumpToSession(string key)
    {
        if (!_sessionCards.TryGetValue(key, out var card)) return;
        card.BringIntoView();
        card.BorderBrush = Accent;
        card.BorderThickness = new Thickness(2);
        var flash = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        flash.Tick += (_, _) => { flash.Stop(); ApplyHighlight(); };
        flash.Start();
    }

    // The panel buttons carry their review kind in Tag ("sessions" or "github").
    private ReviewSlot SlotOf(object sender) => Slot((sender as FrameworkElement)?.Tag as string == "github" ? ReviewKind.GitHub : ReviewKind.Interaction);

    private void CloseReport_Click(object sender, RoutedEventArgs e)
    {
        SlotOf(sender).Panel.Visibility = Visibility.Collapsed;
        UpdateReportsColumn();
    }

    private void AddRules_Click(object sender, RoutedEventArgs e)
    {
        if (SlotOf(sender).Result is not { } review) return;
        var file = Services.Instructions.InstructionFiles.GlobalFiles().First(f => f.Cli == Services.Instructions.InstructionCli.ClaudeCode).Path;
        string text;
        try { text = File.Exists(file) ? File.ReadAllText(file) : ""; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { text = ""; }
        var suggestion = TimelineReview.ToSuggestion(review.Report, text);
        if (suggestion.Edits.Count == 0)
        {
            StatusText.Text = suggestion.Summary;
            return;
        }
        SuggestionsRequested?.Invoke(suggestion);
    }

    private void OpenReport_Click(object sender, RoutedEventArgs e) => OpenFile(SlotOf(sender).Result?.ReportPath);
    private void OpenDigest_Click(object sender, RoutedEventArgs e) => OpenFile(SlotOf(sender).Result?.DigestPath);

    private void OpenFile(string? path)
    {
        if (path is null || !File.Exists(path)) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { StatusText.Text = "Could not open " + path + ": " + ex.Message; }
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
