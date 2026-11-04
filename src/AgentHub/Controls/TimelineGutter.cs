using System.Windows;
using System.Windows.Media;
using AgentHub.Services.Timeline;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace AgentHub.Controls;

/// <summary>
/// Draws one timeline row's share of the graph beside its card: the trunk, every lane running through the row, a fork
/// curve where a lane leaves the trunk, a merge curve where it rejoins, and the row's node. It stretches with the row,
/// so an expanded card simply gets longer lines.
/// </summary>
public sealed class TimelineGutter : FrameworkElement
{
    public const double LaneWidth = 22;
    /// <summary>Where the node sits below the row's top, level with the card's title line.</summary>
    public const double NodeY = 19;
    private const double LeftPad = 11;
    private const double CurveRun = 22;

    private static readonly Brush TrunkBrush = Frozen("#2A3A52");
    private static readonly Brush CommitsNodeBrush = Frozen("#7E8CA6");
    private static readonly Brush StartFill = Frozen("#0C121C");
    private static readonly Brush[] LaneBrushes =
        new[] { "#2FD4F5", "#C084FC", "#34D399", "#FBBF24", "#60A5FA", "#F472B6", "#FB923C", "#A3E635" }.Select(Frozen).ToArray();
    private static readonly Pen TrunkPen = FrozenPen(TrunkBrush, 2);
    private static readonly Pen[] LanePens = LaneBrushes.Select(b => FrozenPen(b, 2)).ToArray();
    private static readonly Pen[] RingPens = LaneBrushes.Select(b => FrozenPen(b, 1.5)).ToArray();

    public static readonly DependencyProperty RowProperty = DependencyProperty.Register(
        nameof(Row), typeof(TimelineRow), typeof(TimelineGutter), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ColumnsProperty = DependencyProperty.Register(
        nameof(Columns), typeof(int), typeof(TimelineGutter),
        new FrameworkPropertyMetadata(1, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

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

    public static Brush LaneBrush(int column) => column == 0 ? LaneBrushes[0] : LaneBrushes[column % LaneBrushes.Length];

    protected override Size MeasureOverride(Size availableSize) => new(LaneWidth * Math.Max(1, Columns), 0);

    protected override void OnRender(DrawingContext dc)
    {
        if (Row is not { } row) return;
        var height = ActualHeight;
        if (height <= 0) return;

        var trunkX = X(0);
        // A lane rejoining the trunk at the bottom needs the trunk to reach the bottom, even in the last row.
        var trunkToBottom = row.Lines.Any(l => l.MergeAtBottom);

        foreach (var line in row.Lines)
        {
            var x = X(line.Column);
            var pen = line.Column == 0 ? TrunkPen : LanePens[line.Column % LanePens.Length];
            var top = line.FromTop ? 0 : NodeY;
            var bottom = line.ToBottom || (line.Column == 0 && trunkToBottom) ? height : NodeY;
            if (line.MergeAtBottom) bottom = Math.Max(NodeY, height - CurveRun);

            if (line.Fork) dc.DrawGeometry(null, pen, Curve(new Point(trunkX, 0), new Point(x, NodeY)));
            if (bottom > top) dc.DrawLine(pen, new Point(x, top), new Point(x, bottom));
            if (line.MergeAtBottom) dc.DrawGeometry(null, pen, Curve(new Point(x, bottom), new Point(trunkX, height)));
        }

        var centre = new Point(X(row.Column), NodeY);
        switch (row.Kind)
        {
            case TimelineRowKind.Start:
                dc.DrawEllipse(StartFill, FrozenPen(LaneBrushes[0], 2), centre, 5.5, 5.5);
                break;
            case TimelineRowKind.Commits:
                dc.DrawEllipse(CommitsNodeBrush, null, centre, 3.5, 3.5);
                break;
            default:
                var index = row.Column % LaneBrushes.Length;
                dc.DrawEllipse(LaneBrushes[index], null, centre, 5, 5);
                if (row.IsLive) dc.DrawEllipse(null, RingPens[index], centre, 8.5, 8.5);
                break;
        }
    }

    private static double X(int column) => LeftPad + column * LaneWidth;

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

    private static Pen FrozenPen(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        return pen;
    }
}
