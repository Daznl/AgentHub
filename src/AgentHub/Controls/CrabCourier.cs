using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Brushes = System.Windows.Media.Brushes;
using Image = System.Windows.Controls.Image;
using Point = System.Windows.Point;

namespace AgentHub.Controls;

/// <summary>
/// There is one crab for all of AgentHub. It lives in the window being worked in: when another AgentHub window takes the
/// focus (the main window, a Context window), the crab leaves its spot, makes its way across its own window to the
/// border, jumps over the border into the other window and walks on to that window's crab spot, where it settles as
/// that window's helper button. Each window has a <see cref="CrabMascot"/> marking its spot; only the host's shows. The
/// trip is drawn in a click-through window on top of everything. With animations off it simply appears at the new spot.
/// </summary>
public static class CrabCourier
{
    private static CrabMascot? _first;   // the main window's spot: where the crab starts and returns to
    private static CrabMascot? _host;    // where the crab is now
    private static CrabMascot? _bound;   // where it is heading, while a trip is under way
    private static CrabMascot? _next;    // asked for during a trip
    private static CrabFlight? _flight;

    /// <summary>The spot the crab is at (or, mid-trip, the one it left).</summary>
    public static CrabMascot? Host => _host;

    /// <summary>Every spot registers once; the first is the crab's home, the others start empty.</summary>
    public static void Register(CrabMascot spot)
    {
        if (_first is null) { _first = _host = spot; return; }
        if (spot != _host) spot.Depart();
    }

    /// <summary>Brings the crab to this spot, once the spot has been laid out.</summary>
    public static void MoveTo(CrabMascot target)
    {
        if (_flight is not null) { _next = target == _bound ? null : target; return; }
        if (_host is null || _host == target) return;
        target.Dispatcher.BeginInvoke(() => Start(target), DispatcherPriority.Loaded);
    }

    /// <summary>A spot going away (its window closing): a crab there, or on its way there, goes home.</summary>
    public static void Evict(CrabMascot spot)
    {
        if (_first is null || spot == _first) return;
        if (_bound == spot) { _next = _first; return; }
        if (_host == spot) MoveTo(_first);
    }

    private static void Start(CrabMascot target)
    {
        if (_flight is not null) { _next = target == _bound ? null : target; return; }
        if (_host is null || _host == target) return;
        var to = target.CellOnScreen(home: true);
        if (to is null)
        {
            // Not laid out yet (a window still opening): try again when it is
            void Once(object s, SizeChangedEventArgs e) { target.SizeChanged -= Once; Start(target); }
            target.SizeChanged += Once;
            return;
        }
        var host = _host;
        var from = host.CellOnScreen(home: false);
        var source = WindowOnScreen(host);
        var dest = WindowOnScreen(target);
        host.Depart();
        _host = null;
        if (from is null || source is null || dest is null || !host.IsMoving || !target.IsMoving)
        {
            Settle(target);
            return;
        }
        _bound = target;
        _flight = new CrabFlight(from.Value, to.Value, source.Value, dest.Value, host.SpriteScale, host.Dpi, () => { _flight = null; Settle(target); });
        _flight.Show();
    }

    private static void Settle(CrabMascot target)
    {
        _host = target;
        _bound = null;
        target.Arrive();
        if (_next is { } next) { _next = null; MoveTo(next); }
    }

    /// <summary>The spot's window on screen, in physical pixels.</summary>
    private static Rect? WindowOnScreen(CrabMascot spot)
    {
        if (Window.GetWindow(spot) is not { } window) return null;
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero || !GetWindowRect(handle, out var r)) return null;
        return new Rect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);
}

/// <summary>
/// The crab in transit: a transparent, topmost, click-through window over the straight line between the two spots. The
/// line is cut where it crosses a window border: inside a window the crab scuttles or rolls along it at a walking pace;
/// at the border it jumps (the hop frames, in an arc) over to the other window and walks on from there.
/// </summary>
internal sealed class CrabFlight : Window
{
    private const double WalkSpeed = 320;     // screen pixels per second along a window
    private const double JumpSpeed = 520;     // across the border
    private const double Pad = 160;           // room around the path for the jump arc and the sprite cell, in screen pixels
    private const int WsExTransparent = 0x20, WsExToolWindow = 0x80, WsExNoActivate = 0x08000000, GwlExStyle = -20;

    private sealed record Leg(Point From, Point To, bool Jump, string Clip, TimeSpan Duration);

    private readonly Rect _bounds;
    private readonly double _dpi;
    private readonly Action _done;
    private readonly Image _sprite;
    private readonly List<Leg> _legs = [];
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly DateTime _started = DateTime.UtcNow;
    private bool _finished;

    public CrabFlight(Rect from, Rect to, Rect source, Rect dest, double scale, double dpi, Action done)
    {
        _dpi = dpi;
        _done = done;
        _bounds = Rect.Union(from, to);
        _bounds.Inflate(Pad, Pad);
        PlanLegs(from, to, source, dest);

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        IsHitTestVisible = false;
        Focusable = false;
        Title = "AgentHub crab in transit";
        Left = _bounds.X / dpi;
        Top = _bounds.Y / dpi;
        Width = _bounds.Width / dpi;
        Height = _bounds.Height / dpi;

        _sprite = new Image
        {
            Width = CrabMascot.CellSize * scale,
            Height = CrabMascot.CellSize * scale,
            Stretch = Stretch.Fill,
            SnapsToDevicePixels = true,
            Source = CrabMascot.Frames(_legs[0].Clip)[0],
        };
        RenderOptions.SetBitmapScalingMode(_sprite, BitmapScalingMode.NearestNeighbor);
        RenderOptions.SetEdgeMode(_sprite, EdgeMode.Aliased);
        var stage = new Canvas { UseLayoutRounding = true };
        stage.Children.Add(_sprite);
        Content = stage;
        Place(from.TopLeft);

        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            SetWindowLong(handle, GwlExStyle, GetWindowLong(handle, GwlExStyle) | WsExTransparent | WsExToolWindow | WsExNoActivate);
        };
        _timer.Tick += (_, _) => Tick();
        Loaded += (_, _) => _timer.Start();
    }

    // The path is the straight line between the two cells. Where the crab's centre leaves its own window or enters the
    // other (whichever comes first) the jump starts; where the last of those crossings is, it ends. Windows that overlap
    // give a short hop at the border; a gap between them gives a leap across it.
    private void PlanLegs(Rect from, Rect to, Rect source, Rect dest)
    {
        var half = new Vector(from.Width / 2, from.Height / 2);
        var a = from.TopLeft + half;
        var b = to.TopLeft + half;
        var walkClip = new Random().Next(2) == 0 ? "scuttle" : "roll";
        if (dest.Contains(a))
        {
            // The window it is going to has just come to the front over the spot it is leaving: it comes out from under
            // that window's nearest edge, hops in over the border there and walks on to the spot
            var edge = NearestOnBorder(a, dest);
            var start = from.TopLeft + (edge - a);
            _legs.Add(new Leg(start, start, true, "hop", TimeSpan.FromSeconds(0.55)));
            Walk(start, to.TopLeft, walkClip);
            return;
        }
        var crossings = Crossings(a, b, source).Concat(Crossings(a, b, dest)).Where(t => t is > 0.001 and < 0.999).OrderBy(t => t).ToList();
        if (crossings.Count == 0)
        {
            Walk(from.TopLeft, to.TopLeft, walkClip);
            return;
        }
        var jumpStart = crossings[0];
        var jumpEnd = crossings[^1];
        var path = to.TopLeft - from.TopLeft;
        var p1 = from.TopLeft + path * jumpStart;
        var p2 = from.TopLeft + path * jumpEnd;
        Walk(from.TopLeft, p1, walkClip);
        var leap = (p2 - p1).Length;
        _legs.Add(new Leg(p1, p2, true, "hop", TimeSpan.FromSeconds(Math.Clamp(leap / JumpSpeed, 0.45, 1.2))));
        Walk(p2, to.TopLeft, walkClip == "roll" ? "scuttle" : "roll");   // the other gait on the far side
    }

    private void Walk(Point from, Point to, string clip)
    {
        var length = (to - from).Length;
        if (length < 1) return;
        _legs.Add(new Leg(from, to, false, clip, TimeSpan.FromSeconds(Math.Clamp(length / WalkSpeed, 0.25, 2.5))));
    }

    /// <summary>The point on the border of <paramref name="rect"/> closest to <paramref name="p"/>, which lies inside it.</summary>
    private static Point NearestOnBorder(Point p, Rect rect)
    {
        var sides = new[]
        {
            (dist: p.X - rect.Left, at: new Point(rect.Left, p.Y)),
            (dist: rect.Right - p.X, at: new Point(rect.Right, p.Y)),
            (dist: p.Y - rect.Top, at: new Point(p.X, rect.Top)),
            (dist: rect.Bottom - p.Y, at: new Point(p.X, rect.Bottom)),
        };
        return sides.MinBy(s => s.dist).at;
    }

    /// <summary>Where the segment a→b crosses the border of <paramref name="rect"/>, as fractions of the segment.</summary>
    private static IEnumerable<double> Crossings(Point a, Point b, Rect rect)
    {
        var d = b - a;
        double tEnter = double.NegativeInfinity, tExit = double.PositiveInfinity;
        foreach (var (p, q) in new[] { (-d.X, a.X - rect.Left), (d.X, rect.Right - a.X), (-d.Y, a.Y - rect.Top), (d.Y, rect.Bottom - a.Y) })
        {
            if (Math.Abs(p) < 1e-9)
            {
                if (q < 0) yield break;   // parallel and outside: never crosses
                continue;
            }
            var t = q / p;
            if (p < 0) tEnter = Math.Max(tEnter, t); else tExit = Math.Min(tExit, t);
        }
        if (tEnter > tExit) yield break;
        yield return tEnter;
        yield return tExit;
    }

    private void Place(Point screen)
    {
        Canvas.SetLeft(_sprite, (screen.X - _bounds.X) / _dpi);
        Canvas.SetTop(_sprite, (screen.Y - _bounds.Y) / _dpi);
    }

    private void Tick()
    {
        var elapsed = DateTime.UtcNow - _started;
        var into = elapsed;
        Leg? leg = null;
        foreach (var l in _legs)
        {
            if (into < l.Duration) { leg = l; break; }
            into -= l.Duration;
        }
        if (leg is null) { Place(_legs[^1].To); Finish(); return; }

        var t = into.TotalSeconds / leg.Duration.TotalSeconds;
        var frames = CrabMascot.Frames(leg.Clip);
        double x, y;
        if (leg.Jump)
        {
            // Crouch, leap in an arc, land: the hop frames spread over the jump
            var eased = t < 0.5 ? 2 * t * t : 1 - Math.Pow(-2 * t + 2, 2) / 2;
            var height = Math.Clamp((leg.To - leg.From).Length * 0.35, 36, Pad * 0.7);
            x = leg.From.X + (leg.To.X - leg.From.X) * eased;
            y = leg.From.Y + (leg.To.Y - leg.From.Y) * eased - Math.Sin(Math.PI * t) * height;
            _sprite.Source = frames[Math.Min(frames.Length - 1, (int)(t * frames.Length))];
        }
        else
        {
            x = leg.From.X + (leg.To.X - leg.From.X) * t;
            y = leg.From.Y + (leg.To.Y - leg.From.Y) * t;
            var frameTime = CrabMascot.FrameTime(leg.Clip).TotalMilliseconds;
            _sprite.Source = frames[(int)(into.TotalMilliseconds / frameTime) % frames.Length];
        }
        Place(new Point(x, y));
    }

    private void Finish()
    {
        if (_finished) return;
        _finished = true;
        _timer.Stop();
        Close();
        _done();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        Finish();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}
