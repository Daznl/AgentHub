using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using AgentHub.Services.Orchestrator;
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace AgentHub.Controls;

/// <summary>
/// The run tree beside a pane (owner request 2026-11-18): the advanced template its orchestrator follows, as small cards on a
/// trunk, next to the session the run drives. Done is green, the step the run is at is blue; a click on a card opens it
/// to its kind, its text and how far the run has got with it. Rows from <see cref="RunTreeView"/>; the Advanced templates
/// window remains the place to drive the run from.
/// </summary>
public partial class RunTreePanel : System.Windows.Controls.UserControl
{
    private TemplateRun? _run;
    private string? _stamp;
    private string? _scrolledTo;
    private readonly HashSet<string> _open = [];

    /// <summary>✕ in the tree's corner: the owner wants it hidden.</summary>
    public event Action? CloseRequested;

    public RunTreePanel() => InitializeComponent();

    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();

    /// <summary>Shows where the run stands. Redraws only when something it shows changed; keeps the owner's place in the list.</summary>
    public void Show(TemplateRun run)
    {
        var stamp = RunTreeView.Stamp(run);
        if (ReferenceEquals(run, _run) && stamp == _stamp) return;
        if (!ReferenceEquals(run, _run)) _open.Clear(); // another run, or the template given to this one changed
        _run = run;
        _stamp = stamp;
        SummaryText.Text = RunTreeView.Summary(run);

        var offset = Scroller.VerticalOffset;
        RowsPanel.Children.Clear();
        var rows = RunTreeView.Rows(run);
        FrameworkElement? current = null;
        for (var i = 0; i < rows.Count; i++)
        {
            var built = BuildRow(rows[i], first: i == 0, last: i == rows.Count - 1);
            RowsPanel.Children.Add(built);
            if (rows[i] is { Kind: TreeRowKind.Step, State: RunTreeState.Current }) current = built;
        }

        // Follow the run down the tree once per step, so the owner can scroll away from it in between.
        var at = run.Current?.Step.Id;
        if (current is not null && at != _scrolledTo)
        {
            _scrolledTo = at;
            _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                () => current.BringIntoView(new Rect(0, -40, current.ActualWidth, current.ActualHeight + 100)));
        }
        else _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () => Scroller.ScrollToVerticalOffset(offset));
    }

    // ---- Drawing ----

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    // Green and blue as in the Advanced templates window, so a step reads the same in both; the rest in the orchestrator's purple.
    private static readonly Brush Line = Frozen(0x2E, 0x26, 0x52);
    private static readonly Brush Faint = Frozen(0x5A, 0x6B, 0x85);
    private static readonly Brush Muted = Frozen(0x7E, 0x8C, 0xA6);
    private static readonly Brush Text = Frozen(0xE8, 0xF1, 0xFB);
    private static readonly Brush Blue = Frozen(0x2F, 0xD4, 0xF5);   // the step the run is at (the app's accent)
    private static readonly Brush Green = Frozen(0x34, 0xE5, 0xA8);  // done
    private static readonly Brush Amber = Frozen(0xFB, 0xBF, 0x24);
    private static readonly Brush StageDot = Frozen(0xA7, 0x8B, 0xFA);
    private static readonly Brush Card = Frozen(0x0F, 0x0C, 0x1F);
    private static readonly Brush CardBorder = Frozen(0x2A, 0x22, 0x4A);
    private static readonly Brush CardHover = Frozen(0x6D, 0x5B, 0xC0);
    private static readonly Brush NodeFill = Frozen(0x15, 0x11, 0x29);

    private const double Gutter = 16;

    /// <summary>One row: the trunk and the row's node on the left, its card on the right; the card opens on a click.</summary>
    private Button BuildRow(RunTreeRow row, bool first, bool last)
    {
        var stage = row.Kind == TreeRowKind.Stage;
        var (nodeColour, cardBorder) = row.State switch
        {
            RunTreeState.Done => (Green, CardBorder),
            RunTreeState.Current => (Blue, Blue),
            RunTreeState.Skipped => (Faint, CardBorder),
            _ => (stage ? StageDot : Muted, CardBorder),
        };

        // From the row's top: level with the first line of the row's text.
        var centre = stage ? 15.5 : 11.5;
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Gutter) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());

        // The trunk joins every node, from the first row's to the last's.
        var trunk = new Rectangle
        {
            Width = 2, Fill = Line, HorizontalAlignment = HorizontalAlignment.Center, SnapsToDevicePixels = true,
            VerticalAlignment = last ? VerticalAlignment.Top : VerticalAlignment.Stretch,
            Margin = new Thickness(0, first ? centre : 0, 0, 0),
        };
        if (last) trunk.Height = first ? 0 : centre;
        grid.Children.Add(trunk);

        var size = stage ? 10.0 : 8.0;
        var done = row.State == RunTreeState.Done;
        var node = new Ellipse
        {
            Width = size, Height = size, StrokeThickness = 2, Stroke = nodeColour,
            Fill = stage || done || row.State == RunTreeState.Current ? nodeColour : NodeFill,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, centre - size / 2, 0, 0),
        };
        grid.Children.Add(node);

        var content = stage ? StageCard(row, nodeColour) : StepCard(row, cardBorder);
        Grid.SetColumn(content.Card, 1);
        grid.Children.Add(content.Card);

        var button = new Button { Style = (Style)Resources["RowButton"], Content = grid, ToolTip = row.Title };
        AutomationProperties.SetName(button, (stage ? "Stage: " : "Step: ") + row.Title + " · " + StateWord(row.State));
        void ShowOpen() => content.Details.Visibility = _open.Contains(row.Id) ? Visibility.Visible : Visibility.Collapsed;
        ShowOpen();
        button.Click += (_, _) =>
        {
            if (!_open.Remove(row.Id)) _open.Add(row.Id);
            ShowOpen();
        };
        if (!stage)
        {
            void Hover() => content.Card.BorderBrush = row.State == RunTreeState.Current ? Blue : button.IsMouseOver || button.IsKeyboardFocused ? CardHover : cardBorder;
            button.MouseEnter += (_, _) => Hover();
            button.MouseLeave += (_, _) => Hover();
            button.GotKeyboardFocus += (_, _) => Hover();
            button.LostKeyboardFocus += (_, _) => Hover();
        }
        return button;
    }

    /// <summary>A stage: its title on the trunk, no card; opened, its size and what it waits for.</summary>
    private static (Border Card, FrameworkElement Details) StageCard(RunTreeRow row, Brush colour)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = row.Title, Foreground = row.State == RunTreeState.Pending ? Text : colour, FontSize = 11, FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        var details = new TextBlock { Text = row.Detail ?? "", Foreground = Muted, FontSize = 10, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 0) };
        panel.Children.Add(details);
        var card = new Border { Padding = new Thickness(4, 4, 2, 3), Margin = new Thickness(2, 4, 0, 1), Background = System.Windows.Media.Brushes.Transparent, Child = panel };
        return (card, details);
    }

    /// <summary>A step: its kind's icon and its title on one line; opened, the whole title, its kind, how far it is and its text.</summary>
    private static (Border Card, FrameworkElement Details) StepCard(RunTreeRow row, Brush border)
    {
        var head = new DockPanel { LastChildFill = true };
        var icon = new TextBlock { Text = row.Icon, FontFamily = new System.Windows.Media.FontFamily("Segoe UI Emoji"), FontSize = 10, Width = 17, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(icon, Dock.Left);
        head.Children.Add(icon);
        if (row.State is RunTreeState.Done or RunTreeState.Current)
        {
            var mark = new TextBlock
            {
                Text = row.State == RunTreeState.Done ? "✓" : "●", Foreground = row.State == RunTreeState.Done ? Green : Blue,
                FontSize = 10, FontWeight = FontWeights.Bold, Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            };
            DockPanel.SetDock(mark, Dock.Right);
            head.Children.Add(mark);
        }
        var title = new TextBlock
        {
            Text = row.Title, Foreground = row.State == RunTreeState.Skipped ? Faint : Text, FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
        };
        head.Children.Add(title);

        var details = new StackPanel { Margin = new Thickness(0, 4, 0, 1) };
        // The title in full, when one line cut it short.
        details.Children.Add(new TextBlock { Text = row.Title, Foreground = Text, FontSize = 11, TextWrapping = TextWrapping.Wrap });
        details.Children.Add(new TextBlock
        {
            Text = ((row.Label ?? "") + " · " + StateWord(row.State)).ToUpperInvariant(),
            Foreground = row.State switch { RunTreeState.Done => Green, RunTreeState.Current => Blue, _ => Faint },
            FontSize = 9, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 3, 0, 0),
        });
        if (row.Detail is { } detail) details.Children.Add(new TextBlock { Text = detail, Foreground = Muted, FontSize = 10.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) });
        if (row.Escalate) details.Children.Add(new TextBlock { Text = "⚑ pauses for you", Foreground = Amber, FontSize = 10, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 3, 0, 0) });

        var body = new StackPanel();
        body.Children.Add(head);
        body.Children.Add(details);
        var card = new Border
        {
            Background = Card, BorderBrush = border, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5),
            Padding = new Thickness(5, 2, 6, 3), Margin = new Thickness(4, 1, 0, 1), Child = body,
            Opacity = row.State switch { RunTreeState.Done => 0.85, RunTreeState.Skipped => 0.6, _ => 1.0 },
        };
        return (card, details);
    }

    private static string StateWord(RunTreeState state) => state switch
    {
        RunTreeState.Done => "done",
        RunTreeState.Current => "now",
        RunTreeState.Skipped => "skipped",
        _ => "to do",
    };
}
