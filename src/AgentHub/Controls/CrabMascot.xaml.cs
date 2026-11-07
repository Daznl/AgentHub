using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using UserControl = System.Windows.Controls.UserControl;

namespace AgentHub.Controls;

/// <summary>
/// The AgentHub crab: a pixel-art mascot that lives at the top right of the header, beside the buttons, and is the
/// button that opens the AgentHub helper (click, or Enter / Space when focused; <see cref="Poked"/>). It idles and
/// blinks, now and then scuttles, rolls, pinches, waves or dances along the free strip of header, and dozes off when
/// AgentHub has had no input for a while. While <see cref="IsActive"/> (helper open) it glows and stays at home. Sprites are 80x80 cells in strips under <c>Assets/Crab</c>
/// (made with PixelLab, see <c>docs/brand/crab/README.md</c>); they are drawn at a whole number of screen pixels per
/// sprite pixel so they stay crisp at every Windows display scale. Motion is off when Windows animations are off.
/// </summary>
public partial class CrabMascot : UserControl
{
    private const int Cell = 80;            // sprite cell, in sprite pixels
    private const int BodyLeft = 12;        // first opaque column of the resting crab inside its cell
    private const int BodyRight = 68;       // one past its last opaque column
    private const int Feet = 76;            // one past its lowest opaque row
    private const double Gap = 6;           // space kept to the logo and to the header buttons, in DIP
    private const int RollStride = 6;       // sprite pixels travelled per mid-tumble roll frame (a walk step is 2)
    private static readonly TimeSpan SleepAfter = TimeSpan.FromMinutes(5);

    private sealed record Sheet(BitmapSource[] Frames, TimeSpan FrameTime);

    private static readonly Dictionary<string, Sheet> Clips = new()
    {
        ["idle"] = Load("idle", 150),
        ["scuttle"] = Load("scuttle", 80),
        ["pinch"] = Load("pinch", 90),
        ["wave"] = Load("wave", 110),
        ["roll"] = Load("roll", 75),
        ["hop"] = Load("hop", 70),
        ["dance"] = Load("dance", 110),
        ["sleep"] = Load("sleep", 170),
    };

    private enum Mode { Resting, Acting, Walking, Asleep }

    private readonly DispatcherTimer _timer = new(DispatcherPriority.Render);
    private readonly Random _random = new();
    private Window? _window;
    private bool _animated = true;
    private double _scale = 1;               // DIP per sprite pixel
    private double _x;                       // left of the sprite cell, in DIP from this control's left edge
    private bool _placed;
    private double _home;                    // Home at the last layout, to tell whether the crab is sitting there

    private Mode _mode = Mode.Resting;
    private Sheet _clip = Clips["idle"];
    private int _frame;
    private bool _reverse;
    private bool _loop;
    private Action? _then;
    private double? _target;                 // where the current walk or roll is heading
    private Func<int, bool> _movesOn = _ => false;
    private DateTime _restUntil;
    private DateTime _nextOuting;
    private DateTime _lastInput = DateTime.UtcNow;
    private bool _active;

    // An orange halo, like the glow of the logo artwork: soft on hover or keyboard focus, full while active.
    private readonly System.Windows.Media.Effects.DropShadowEffect _glow =
        new() { Color = System.Windows.Media.Color.FromRgb(0xFF, 0x7A, 0x45), ShadowDepth = 0, BlurRadius = 16, Opacity = 0 };

    public CrabMascot()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => Step();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += (_, _) => Layout();
        Sprite.DpiChanged += (_, _) => Layout();  // moved to a screen with another display scale
        Sprite.MouseEnter += (_, _) => UpdateGlow();
        Sprite.MouseLeave += (_, _) => UpdateGlow();
        Sprite.GotKeyboardFocus += (_, _) => UpdateGlow();
        Sprite.LostKeyboardFocus += (_, _) => UpdateGlow();
        Sprite.Invoked = Press;
        Sprite.Status = () => _active ? "Helper open" : "";
        Show(Clips["idle"].Frames[0]);
    }

    /// <summary>Raised when the crab is clicked, or Enter or Space is pressed while it has keyboard focus.</summary>
    public event Action? Poked;

    /// <summary>Lit and staying at home, as a pressed toggle button would; the owner sets it while the helper is open.</summary>
    public bool IsActive
    {
        get => _active;
        set
        {
            if (_active == value) return;
            _active = value;
            UpdateGlow();
        }
    }

    private void UpdateGlow()
    {
        _glow.Opacity = _active ? 0.95 : Sprite.IsMouseOver || Sprite.IsKeyboardFocused ? 0.6 : 0;
        Sprite.Effect = _glow.Opacity > 0 ? _glow : null;
    }

    /// <summary>Width at the right of this control that the crab must not walk into (the header buttons).</summary>
    public double ReservedRight
    {
        get => _reservedRight;
        set { _reservedRight = value; Layout(); }
    }
    private double _reservedRight;

    /// <summary>Turns the crab's motion on or off. Off, it sits still at home in its resting pose.</summary>
    public void SetAnimated(bool animated)
    {
        _animated = animated;
        if (IsLoaded) Restart();
    }

    private bool Moving => _animated && SystemParameters.ClientAreaAnimation;

    // Sprite-cell positions bounding the free strip: just right of the logo, and just left of the header buttons.
    private double Leftmost => Gap - BodyLeft * _scale;

    private double Rightmost => Math.Max(Leftmost, ActualWidth - _reservedRight - Gap - BodyRight * _scale);

    /// <summary>The crab lives at the top right, beside the header buttons, and wanders left from there.</summary>
    private double Home => Rightmost;

    private bool HasRoom => Rightmost - Leftmost >= 40;

    private static Sheet Load(string name, int frameMs)
    {
        var strip = new BitmapImage();
        strip.BeginInit();
        strip.UriSource = new Uri($"pack://application:,,,/Assets/Crab/{name}.png");
        strip.CacheOption = BitmapCacheOption.OnLoad;
        strip.EndInit();
        strip.Freeze();
        var frames = new BitmapSource[strip.PixelWidth / Cell];
        for (var i = 0; i < frames.Length; i++)
        {
            var frame = new CroppedBitmap(strip, new Int32Rect(i * Cell, 0, Cell, Cell));
            frame.Freeze();
            frames[i] = frame;
        }
        return new Sheet(frames, TimeSpan.FromMilliseconds(frameMs));
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _window = Window.GetWindow(this);
        if (_window is not null) _window.StateChanged += OnWindowStateChanged;
        InputManager.Current.PostProcessInput += OnAnyInput;
        Layout();
        Restart();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _timer.Stop();
        if (_window is not null) _window.StateChanged -= OnWindowStateChanged;
        InputManager.Current.PostProcessInput -= OnAnyInput;
    }

    // No frames are drawn while AgentHub is minimised.
    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        if (_window?.WindowState == WindowState.Minimized) _timer.Stop();
        else if (Moving) _timer.Start();
    }

    private void OnAnyInput(object sender, ProcessInputEventArgs e)
    {
        if (e.StagingItem.Input is not (System.Windows.Input.MouseEventArgs or KeyboardEventArgs)) return;
        _lastInput = DateTime.UtcNow;
        if (_mode == Mode.Asleep) WakeUp();
    }

    /// <summary>Whole screen pixels per sprite pixel, sized so the crab sits comfortably beside the 72 DIP logo.</summary>
    private void Layout()
    {
        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var pixels = Math.Max(1, Math.Floor(dpi + 0.25));
        _scale = pixels / dpi;
        Sprite.Width = Sprite.Height = Cell * _scale;
        Canvas.SetTop(Sprite, ActualHeight - Feet * _scale);

        // At home it stays at home as the window resizes; out wandering it is kept inside the strip.
        var wasHome = Math.Abs(_x - _home) < 0.5 && _target is null;
        if (!_placed || wasHome) { _x = Home; _placed = true; }
        else _x = Math.Clamp(_x, Leftmost, Rightmost);
        if (_target is { } t) _target = Math.Clamp(t, Leftmost, Rightmost);
        _home = Home;
        Canvas.SetLeft(Sprite, _x);
    }

    private void Restart()
    {
        _timer.Stop();
        _x = _home = Home;
        Canvas.SetLeft(Sprite, _x);
        _target = null;
        Show(Clips["idle"].Frames[0]);
        if (!Moving) return;
        _nextOuting = DateTime.UtcNow + TimeSpan.FromSeconds(_random.Next(6, 14));
        Rest();
        if (_window?.WindowState != WindowState.Minimized) _timer.Start();
    }

    private void Show(BitmapSource frame) => Sprite.Source = frame;

    private void Play(string clip, Mode mode, Action? then = null, bool loop = false, bool reverse = false,
        double? target = null, Func<int, bool>? movesOn = null)
    {
        _clip = Clips[clip];
        _mode = mode;
        _then = then;
        _loop = loop;
        _reverse = reverse;
        _target = target;
        _movesOn = movesOn ?? (_ => target is not null);
        _frame = reverse ? _clip.Frames.Length - 1 : 0;
        _timer.Interval = _clip.FrameTime;
        Show(_clip.Frames[_frame]);
    }

    private void Rest()
    {
        _mode = Mode.Resting;
        _target = null;
        _timer.Interval = Clips["idle"].FrameTime;
        Show(Clips["idle"].Frames[0]);
        _restUntil = DateTime.UtcNow + TimeSpan.FromMilliseconds(_random.Next(1500, 4500));
    }

    private void Step()
    {
        if (_mode == Mode.Asleep) return;

        if (_mode == Mode.Resting)
        {
            var now = DateTime.UtcNow;
            if (now < _restUntil) return;
            if (now - _lastInput > SleepAfter) { Play("sleep", Mode.Acting, () => _mode = Mode.Asleep); return; }
            // While the helper is open the crab is its pressed button: it goes back home and stays there
            if (_active && Math.Abs(_x - Home) > 0.5) { Play("scuttle", Mode.Walking, loop: true, target: Home); return; }
            if (now >= _nextOuting) { _nextOuting = now + TimeSpan.FromSeconds(_random.Next(12, 32)); GoOut(); return; }
            Play("idle", Mode.Acting, Rest);  // breathe and blink between outings
            return;
        }

        if (_target is { } target && _movesOn(_frame))
        {
            var stride = (_clip == Clips["roll"] ? RollStride : 2) * _scale;
            _x = Math.Abs(target - _x) <= stride ? target : _x + Math.Sign(target - _x) * stride;
            Canvas.SetLeft(Sprite, _x);
            if (_mode == Mode.Walking && _x == target) { Rest(); return; }
        }

        var last = _reverse ? 0 : _clip.Frames.Length - 1;
        if (_frame == last)
        {
            if (_loop) _frame = _reverse ? _clip.Frames.Length - 1 : 0;
            else { var then = _then; _then = null; (then ?? Rest)(); return; }
        }
        else _frame += _reverse ? -1 : 1;
        Show(_clip.Frames[_frame]);
    }

    /// <summary>Something to do between rests: a walk, a roll along the header, or a little show on the spot.</summary>
    private void GoOut()
    {
        var pick = _active ? 1 : _random.NextDouble();  // active: only tricks on the spot
        if (HasRoom && pick < 0.45)
        {
            // Wander somewhere new, or head home when far from it
            var mid = (Leftmost + Rightmost) / 2;
            var target = Math.Abs(Home - _x) > (Rightmost - Leftmost) / 2 && _random.NextDouble() < 0.6
                ? Home
                : Leftmost + _random.NextDouble() * (Rightmost - Leftmost);
            if (Math.Abs(target - _x) < 16 * _scale) target = _x > mid ? Leftmost : Rightmost;
            Play("scuttle", Mode.Walking, loop: true, target: target);
        }
        else if (HasRoom && pick < 0.6)
        {
            // Frames 4 to 10 of the roll are the crab curled up mid-tumble: travel only then
            var reach = 7 * RollStride * _scale;
            var target = _x - reach >= Leftmost && (_x + reach > Rightmost || _random.NextDouble() < 0.5) ? _x - reach : _x + reach;
            Play("roll", Mode.Acting, target: Math.Clamp(target, Leftmost, Rightmost), movesOn: f => f is >= 4 and < 11);
        }
        else
        {
            Play(new[] { "pinch", "wave", "dance", "roll" }[_random.Next(4)], Mode.Acting);
        }
    }

    private void WakeUp()
    {
        _nextOuting = DateTime.UtcNow + TimeSpan.FromSeconds(_random.Next(4, 10));
        Play("sleep", Mode.Acting, reverse: true);
    }

    private void Sprite_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        Press();
    }

    private void Sprite_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Space)) return;
        e.Handled = true;
        Press();
    }

    // The owner flips IsActive from Poked; then a hop, and a wave hello when that opened the helper.
    private void Press()
    {
        Poked?.Invoke();
        if (!Moving) return;
        Play("hop", Mode.Acting, _active ? () => Play("wave", Mode.Acting) : null);
    }
}

/// <summary>The crab's picture, announced to screen readers and UI Automation as a button that can be invoked.</summary>
public sealed class CrabSprite : System.Windows.Controls.Image
{
    internal Action? Invoked { get; set; }

    /// <summary>Read out after the name, e.g. "Helper open".</summary>
    internal Func<string>? Status { get; set; }

    protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    private sealed class Peer(CrabSprite owner)
        : System.Windows.Automation.Peers.FrameworkElementAutomationPeer(owner), System.Windows.Automation.Provider.IInvokeProvider
    {
        protected override System.Windows.Automation.Peers.AutomationControlType GetAutomationControlTypeCore() =>
            System.Windows.Automation.Peers.AutomationControlType.Button;

        protected override string GetClassNameCore() => nameof(CrabSprite);

        protected override string GetItemStatusCore() => owner.Status?.Invoke() ?? "";

        public override object GetPattern(System.Windows.Automation.Peers.PatternInterface patternInterface) =>
            patternInterface == System.Windows.Automation.Peers.PatternInterface.Invoke ? this : base.GetPattern(patternInterface);

        public void Invoke() => owner.Invoked?.Invoke();
    }
}
