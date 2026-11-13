using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AgentHub.Services.Orchestrator;
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using MessageBox = System.Windows.MessageBox;
using Orientation = System.Windows.Controls.Orientation;

namespace AgentHub;

/// <summary>
/// ⏯ Runs: every run of an advanced template kept on this computer. The ones a pane follows now come first, each with
/// its tree a click away; under them the paused ones (a run is paused as soon as nothing follows it: its orchestrator
/// was detached, or AgentHub was closed), to resume on a running session of the project or to delete; then the finished
/// ones. The main window supplies the rows and carries the actions out.
/// </summary>
public partial class RunsWindow : Window
{
    /// <summary>One run in the list.</summary>
    /// <param name="LivePane">The pane following it now ("Terminal 2"); null for a run kept on disk only.</param>
    public sealed record Row(RunRecord Record, string? LivePane);

    /// <summary>A session a paused run can be picked up on.</summary>
    public sealed record Target(string Label, Action Resume);

    public required Func<IReadOnlyList<Row>> Rows { get; init; }
    public required Func<RunRecord, IReadOnlyList<Target>> ResumeTargets { get; init; }
    public required Action<RunRecord> OpenTree { get; init; }
    public required Action<RunRecord> Delete { get; init; }

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(3) };
    private string _shown = "\0";

    public RunsWindow(string storeFolder)
    {
        InitializeComponent();
        Controls.DarkTitleBar.Apply(this);
        Controls.WindowPlacement.KeepOnScreen(this);
        StatusText.Text = "Kept in " + storeFolder;
        Loaded += (_, _) => Refresh();
        Activated += (_, _) => Refresh();
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Closed += (_, _) => _timer.Stop();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            Close();
            e.Handled = true;
        };
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        _shown = "\0";
        Refresh();
    }

    /// <summary>Reads the runs again; the list is redrawn only when something in it changed.</summary>
    public void Refresh()
    {
        if (!IsLoaded) return;
        var rows = Rows();
        var stamp = string.Join("\n", rows.Select(r => $"{r.Record.Id}|{r.LivePane}|{r.Record.Signature()}|{r.Record.Progress}"));
        if (stamp == _shown) return;
        _shown = stamp;

        RowsPanel.Children.Clear();
        EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        void Section(string title, IEnumerable<Row> of)
        {
            var list = of.ToList();
            if (list.Count == 0) return;
            RowsPanel.Children.Add(new TextBlock { Text = $"{title} · {list.Count}", Foreground = Faint, FontSize = 10, FontWeight = FontWeights.Bold, Margin = new Thickness(2, RowsPanel.Children.Count == 0 ? 0 : 14, 0, 6) });
            foreach (var row in list) RowsPanel.Children.Add(Card(row));
        }
        Section("FOLLOWED ON A PANE NOW", rows.Where(r => r.LivePane is not null));
        Section("PAUSED", rows.Where(r => r.LivePane is null && !r.Record.IsFinished));
        Section("FINISHED", rows.Where(r => r.LivePane is null && r.Record.IsFinished));
    }

    // ---- One run ----

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private static readonly Brush Muted = Frozen(0x7E, 0x8C, 0xA6);
    private static readonly Brush Faint = Frozen(0x5A, 0x6B, 0x85);
    private static readonly Brush Amber = Frozen(0xFB, 0xBF, 0x24);
    private static readonly Brush Cyan = Frozen(0x2F, 0xD4, 0xF5);
    private static readonly Brush Green = Frozen(0x34, 0xE5, 0xA8);
    private static readonly Brush Text = Frozen(0xE8, 0xF1, 0xFB);
    private static readonly Brush CardFill = Frozen(0x0F, 0x17, 0x24);
    private static readonly Brush CardBorder = Frozen(0x1B, 0x27, 0x38);
    private static readonly Brush Track = Frozen(0x1B, 0x27, 0x38);
    private static readonly Brush ButtonFill = Frozen(0x16, 0x20, 0x2F);
    private static readonly Brush ButtonText = Frozen(0xB6, 0xC2, 0xD6);
    private static readonly Brush Blue = Frozen(0x25, 0x63, 0xEB);

    /// <summary>The state word of a run and its colour: a run nothing follows is paused, whatever it was doing when it was saved.</summary>
    public static (string Word, bool Waiting) StateOf(Row row)
    {
        var r = row.Record;
        if (r.State == RunState.Finished) return ("FINISHED", false);
        if (row.LivePane is null) return (r.State == RunState.Ready ? "NOT STARTED" : "PAUSED", false);
        return r.State switch
        {
            RunState.Ready => ("READY", false),
            RunState.Running => ("RUNNING", false),
            _ => r.Cause switch
            {
                PauseCause.Owner => ("PAUSED", false),
                PauseCause.Problem => ("STOPPED", true),
                _ => ("WAITING FOR YOU", true),
            },
        };
    }

    private Border Card(Row row)
    {
        var r = row.Record;
        var (word, waiting) = StateOf(row);
        var colour = r.IsFinished ? Green : word == "RUNNING" ? Cyan : waiting || word == "PAUSED" ? Amber : Muted;

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        DockPanel.SetDock(buttons, Dock.Right);
        if (row.LivePane is not null)
            buttons.Children.Add(Action("🗺 Open tree", "Open this run's tree: where it stands, ▶ Go, ⏸ Pause", ButtonFill, ButtonText, r, "Open the tree of", (_, _) => OpenTree(r)));
        else
        {
            if (!r.IsFinished) buttons.Children.Add(Action("▶ Resume…", "Pick this run up on a running session of its project and carry it on from where it stood", Blue, Text, r, "Resume", (s, _) => OfferResume(r, (Button)s)));
            buttons.Children.Add(Action("🗑 Delete", "Forget this run. The project, its template and its sessions are not touched.", ButtonFill, ButtonText, r, "Delete", (_, _) => AskDelete(r)));
        }

        var head = new DockPanel { LastChildFill = true };
        var chip = new Border
        {
            BorderBrush = colour, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(7, 1, 7, 2),
            Margin = new Thickness(0, 0, 9, 0), VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = row.LivePane is null ? word : $"{word} · {row.LivePane}", Foreground = colour, FontSize = 10, FontWeight = FontWeights.Bold },
        };
        DockPanel.SetDock(chip, Dock.Left);
        head.Children.Add(chip);
        head.Children.Add(new TextBlock { Text = r.TemplateName, Foreground = Text, FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });

        var body = new StackPanel();
        body.Children.Add(head);
        body.Children.Add(new TextBlock { Text = r.Progress, Foreground = Text, FontSize = 12, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap });
        if (!r.IsFinished && !string.IsNullOrWhiteSpace(r.Reason) && (row.LivePane is null || r.State == RunState.Paused))
            body.Children.Add(new TextBlock { Text = r.Reason, Foreground = Amber, FontSize = 11.5, Margin = new Thickness(0, 3, 0, 0), TextWrapping = TextWrapping.Wrap });

        // How far it has got, as a bar: done steps of all steps.
        var done = r.StepCount > 0 ? Math.Clamp((double)r.DoneCount / r.StepCount, 0, 1) : 0;
        var bar = new Grid { Height = 4, Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, Width = 260 };
        bar.Children.Add(new Border { Background = Track, CornerRadius = new CornerRadius(2) });
        bar.Children.Add(new Border { Background = r.IsFinished ? Green : Cyan, CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left, Width = 260 * done });
        body.Children.Add(bar);

        var meta = $"{r.DoneCount} of {r.StepCount} steps done · {r.Project} · started {When(r.StartedAt)} · last moved {When(r.UpdatedAt)}";
        body.Children.Add(new TextBlock { Text = meta, Foreground = Muted, FontSize = 11, Margin = new Thickness(0, 6, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = meta });

        var row1 = new DockPanel { LastChildFill = true };
        row1.Children.Add(buttons);
        row1.Children.Add(body);
        return new Border
        {
            Background = CardFill, BorderBrush = CardBorder, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 10, 12, 11), Margin = new Thickness(0, 0, 0, 6), Child = row1,
        };
    }

    private static string When(DateTimeOffset at)
    {
        var local = at.LocalDateTime;
        return local.Date == DateTime.Today ? local.ToString("HH:mm") : local.ToString("d MMM HH:mm");
    }

    private static Button Action(string content, string tip, Brush background, Brush foreground, RunRecord record, string verb, RoutedEventHandler click)
    {
        var button = new Button { Content = content, ToolTip = tip, Background = background, Foreground = foreground, Padding = new Thickness(10, 4, 10, 4), FontSize = 11.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(8, 0, 0, 0) };
        System.Windows.Automation.AutomationProperties.SetName(button, $"{verb} {record.TemplateName}");
        button.Click += click;
        return button;
    }

    /// <summary>The sessions the run can go on: the owner picks one. Without any, says what to do.</summary>
    private void OfferResume(RunRecord record, Button at)
    {
        var targets = ResumeTargets(record);
        if (targets.Count == 0)
        {
            MessageBox.Show(this,
                $"No session to resume this run on.\n\nStart a session in {record.Project} in a Cockpit pane (one without an orchestrator), then press Resume again. " +
                "The run carries on from where it stood; if that step needs the earlier session's memory, have the new one read .agenthub/handoff.md first.",
                "Resume run", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var menu = new ContextMenu { PlacementTarget = at, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var target in targets)
        {
            var item = new MenuItem { Header = target.Label };
            item.Click += (_, _) =>
            {
                target.Resume();
                Refresh();
            };
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private void AskDelete(RunRecord record)
    {
        var text = $"Delete the run of \"{record.TemplateName}\" ({record.Progress})?\n\nOnly the run's record goes: the project, its template and its sessions are not touched. This cannot be undone.";
        if (MessageBox.Show(this, text, "Delete run", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        try { Delete(record); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { StatusText.Text = "Could not delete the run: " + ex.Message; }
        Refresh();
    }
}
