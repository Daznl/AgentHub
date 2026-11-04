using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using AgentHub.Services.Timeline;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using Cursors = System.Windows.Input.Cursors;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace AgentHub.Controls;

/// <summary>
/// Draws one timeline row's share of the graph beside its card: the trunk, every lane running through the row, a fork
/// curve where a lane leaves the trunk, a merge curve where it rejoins, and the row's node. It stretches with the row,
/// so an expanded card simply gets longer lines. Pointing at a lane raises <see cref="LaneHovered"/> with the branch key
/// so the window can light that branch everywhere; <see cref="HighlightKey"/> draws that lane bold and the rest faint.
/// </summary>
public sealed class TimelineGutter : FrameworkElement
{
    /// <summary>Space between lanes: comfortable for a few, tighter when a repository has had many branches alive at once.</summary>
    public static double LaneWidth(int columns) => columns <= 6 ? 22 : Math.Max(12, Math.Floor(150.0 / columns));
    public static double GutterWidth(int columns) => LaneWidth(Math.Max(1, columns)) * Math.Max(1, columns);
    /// <summary>Where the node sits below the row's top, level with the card's title line.</summary>
    public const double NodeY = 19;
    private const double LeftPad = 11;
    private const double CurveRun = 22;
    private const double HitSlack = 7;

    private static readonly Brush TrunkBrush = Frozen("#2A3A52");
    private static readonly Brush TrunkBoldBrush = Frozen("#5A7194");
    private static readonly Brush CommitsNodeBrush = Frozen("#7E8CA6");
    private static readonly Brush StartFill = Frozen("#0C121C");
    // One colour per branch in order of appearance (see TimelineBranch.ColourIndex); after the last it starts again.
    private static readonly Brush[] LaneBrushes =
        new[] { "#2FD4F5", "#C084FC", "#34D399", "#FBBF24", "#60A5FA", "#F472B6", "#FB923C", "#A3E635", "#E879F9", "#2DD4BF", "#F87171", "#FDE047" }.Select(Frozen).ToArray();
    private static readonly Pen TrunkPen = FrozenPen(TrunkBrush, 2), TrunkBold = FrozenPen(TrunkBoldBrush, 3.5), TrunkDim = FrozenPen(Faint(TrunkBrush), 2);
    private static readonly Pen[] LanePens = LaneBrushes.Select(b => FrozenPen(b, 2)).ToArray();
    private static readonly Pen[] LaneBold = LaneBrushes.Select(b => FrozenPen(b, 3.5)).ToArray();
    private static readonly Pen[] LaneDim = LaneBrushes.Select(b => FrozenPen(Faint(b), 2)).ToArray();
    private static readonly Pen[] RingPens = LaneBrushes.Select(b => FrozenPen(b, 1.5)).ToArray();

    public static readonly DependencyProperty RowProperty = DependencyProperty.Register(
        nameof(Row), typeof(TimelineRow), typeof(TimelineGutter), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ColumnsProperty = DependencyProperty.Register(
        nameof(Columns), typeof(int), typeof(TimelineGutter),
        new FrameworkPropertyMetadata(1, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty HighlightKeyProperty = DependencyProperty.Register(
        nameof(HighlightKey), typeof(string), typeof(TimelineGutter), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private string? _hover;

    /// <summary>The branch key under the pointer, or null when it left every lane.</summary>
    public event Action<TimelineGutter, string?>? LaneHovered;
    /// <summary>A click: the branch key of the lane hit, or null for empty gutter.</summary>
    public event Action<TimelineGutter, string?>? LaneClicked;

    public TimelineRow? Row
    {
        get => (TimelineRow?)GetValue(RowProperty);
        set => SetValue(RowProperty, value);
    }

    /// <summary>Lanes in the whole timeline, so every row's gutter is the same width and the cards line up.</summary>
    public int Columns
    {
        get => (int)GetValue(ColumnsProperty);
        set => SetValue(ColumnsProperty, value);
    }

    /// <summary>Branch key drawn bold while everything else is faint; null draws every lane normally.</summary>
    public string? HighlightKey
    {
        get => (string?)GetValue(HighlightKeyProperty);
        set => SetValue(HighlightKeyProperty, value);
    }

    /// <summary>The brush of a lane by its <see cref="TimelineBranch.ColourIndex"/>; 0 is the trunk.</summary>
    public static Brush LaneBrush(int colourIndex) => colourIndex == 0 ? TrunkBoldBrush : LaneBrushes[colourIndex % LaneBrushes.Length];

    protected override Size MeasureOverride(Size availableSize) => new(GutterWidth(Columns), 0);

    protected override void OnRender(DrawingContext dc)
    {
        // Transparent fill so the whole strip takes the pointer, not just the drawn pixels.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (Row is not { } row) return;
        var height = ActualHeight;
        if (height <= 0) return;

        var highlight = HighlightKey;
        var trunkX = X(0);
        // A lane rejoining the trunk at the bottom needs the trunk to reach the bottom, even in the last row.
        var trunkToBottom = row.Lines.Any(l => l.MergeAtBottom);

        foreach (var line in row.Lines)
        {
            var x = X(line.Column);
            var pen = PenFor(line, highlight);
            var top = line.FromTop ? 0 : NodeY;
            var bottom = line.ToBottom || (line.Column == 0 && trunkToBottom) ? height : NodeY;
            if (line.MergeAtBottom) bottom = Math.Max(NodeY, height - CurveRun);

            if (line.Fork) dc.DrawGeometry(null, pen, Curve(new Point(X(line.ForkFrom), 0), new Point(x, NodeY)));
            if (bottom > top) dc.DrawLine(pen, new Point(x, top), new Point(x, bottom));
            if (line.MergeAtBottom) dc.DrawGeometry(null, pen, Curve(new Point(x, bottom), new Point(trunkX, height)));
        }

        var centre = new Point(X(row.Column), NodeY);
        var faded = highlight is not null && row.LaneKey != highlight;
        if (faded) dc.PushOpacity(0.3);
        switch (row.Kind)
        {
            case TimelineRowKind.Start:
                dc.DrawEllipse(StartFill, FrozenPen(LaneBrushes[0], 2), centre, 5.5, 5.5);
                break;
            case TimelineRowKind.Commits:
                dc.DrawEllipse(CommitsNodeBrush, null, centre, 3.5, 3.5);
                break;
            default:
                var index = (row.Branch?.ColourIndex ?? 0) % LaneBrushes.Length;
                // A session without commits sits on the trunk but belongs to no branch: a hollow node says so.
                if (row.IsHollow) dc.DrawEllipse(StartFill, RingPens[index], centre, 4.5, 4.5);
                else dc.DrawEllipse(LaneBrushes[index], null, centre, 5, 5);
                if (row.IsLive) dc.DrawEllipse(null, RingPens[index], centre, 8.5, 8.5);
                break;
        }
        if (faded) dc.Pop();
    }

    private static Pen PenFor(TimelineLine line, string? highlight)
    {
        var trunk = line.Column == 0;
        var index = line.Colour % LanePens.Length;
        if (highlight is null) return trunk ? TrunkPen : LanePens[index];
        if (line.Key == highlight) return trunk ? TrunkBold : LaneBold[index];
        return trunk ? TrunkDim : LaneDim[index];
    }

    // ---- pointer ----

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var key = LaneAt(e.GetPosition(this));
        Cursor = key is null ? null : Cursors.Hand;
        if (key == _hover) return;
        _hover = key;
        LaneHovered?.Invoke(this, key);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover is null) return;
        _hover = null;
        LaneHovered?.Invoke(this, null);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        LaneClicked?.Invoke(this, LaneAt(e.GetPosition(this)));
        e.Handled = true;
    }

    // The lane whose vertical line is nearest the pointer, within a few pixels; curves count by their lane's column.
    private string? LaneAt(Point p)
    {
        if (Row is not { } row) return null;
        string? best = null;
        var bestDistance = HitSlack + 1;
        foreach (var line in row.Lines)
        {
            var distance = Math.Abs(p.X - X(line.Column));
            if (distance <= HitSlack && distance < bestDistance)
            {
                bestDistance = distance;
                best = line.Key;
            }
        }
        return best;
    }

    private double X(int column) => LeftPad + column * LaneWidth(Columns);

    // An S-curve: vertical at both ends, so it joins the straight lane lines without a kink.
    private static StreamGeometry Curve(Point from, Point to)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            var midY = (from.Y + to.Y) / 2;
            ctx.BeginFigure(from, isFilled: false, isClosed: false);
            ctx.BezierTo(new Point(from.X, midY), new Point(to.X, midY), to, isStroked: true, isSmoothJoin: true);
        }
        geometry.Freeze();
        return geometry;
    }

    private static SolidColorBrush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    private static SolidColorBrush Faint(Brush brush)
    {
        var colour = ((SolidColorBrush)brush).Color;
        var faint = new SolidColorBrush(Color.FromArgb(0x48, colour.R, colour.G, colour.B));
        faint.Freeze();
        return faint;
    }

    private static Pen FrozenPen(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        return pen;
    }
}
