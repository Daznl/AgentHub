using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using UserControl = System.Windows.Controls.UserControl;

namespace AgentHub.Controls;

/// <summary>
/// The AgentHub crab: a pixel-art mascot that is the button for every helper in AgentHub (click, or Enter / Space when
/// focused; <see cref="Poked"/>): at the top right of the main window's header, beside the buttons, it opens the
/// AgentHub helper; at the top right of a Context window it opens the Context helper for the file being edited. It idles and
/// blinks, now and then scuttles, rolls, pinches, waves or dances along the free strip of header, pulls a face (a wink, a
/// laugh, sunglasses, a pirate hat…), and dozes off when AgentHub has had no input for a while. A roll leaves it dizzy for a
/// moment; finished work earns a party hat. While <see cref="IsActive"/> (helper open) it glows and stays at home; while
/// <see cref="IsBusy"/> (a Timeline review running) it paces and pinches. Sprites are 80x80 cells in strips under <c>Assets/Crab</c>
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
        ["faces"] = Load("faces", 1300),   // one still per cell, held for a moment (see Face)
    };

    /// <summary>The cells of <c>faces.png</c>: the mascot's spare expressions from the PixelLab candidates sheet, in sheet order.</summary>
    private enum Face { Smile, Wink, Laugh, Hearts, Shocked, Angry, Sad, Sleepy, Tongue, Dizzy, Pirate, Party, Sunglasses, Shy, Salute, Crown }

    /// <summary>Faces pulled at random between outings: the cheerful ones. Shocked, angry, sad and sleepy wait for a reason.</summary>
    private static readonly Face[] IdleFaces = [Face.Wink, Face.Laugh, Face.Hearts, Face.Tongue, Face.Pirate, Face.Sunglasses, Face.Shy, Face.Salute, Face.Crown];

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
        Sprite.Status = () => _busy ? "Working" : _active ? "Helper open" : _face is { } face ? "Face: " + face : "";
        // Size the sprite before its first render: a size set later only showed once the frame changed (the crab drew at
        // its natural 80 DIP, low and off the right edge of the Context window, for its first few seconds).
        Layout();
        Show(Clips["idle"].Frames[0]);
    }

    /// <summary>Raised when the crab is clicked, or Enter or Space is pressed while it has keyboard focus.</summary>
    public event Action? Poked;

    /// <summary>
    /// What this crab opens, as screen readers and UI Automation announce it ("AgentHub helper", "Context helper"). The same
    /// crab lives in more than one window, each one the helper for that place.
    /// </summary>
    public string HelperName
    {
        get => System.Windows.Automation.AutomationProperties.GetName(Sprite);
        set => System.Windows.Automation.AutomationProperties.SetName(Sprite, value);
    }

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

    /// <summary>
    /// Working on something the user asked for (a Timeline review, say): the crab paces the strip and pinches at each
    /// turn until this is cleared, so the window shows the work is still going. It never dozes off while busy. Where
    /// motion is off it sits still as always; the owner's status line says what is happening.
    /// </summary>
    public bool IsBusy
    {
        get => _busy;
        set
        {
            if (_busy == value) return;
            _busy = value;
            if (!IsLoaded || !Moving) return;
            if (_busy)
            {
                if (_mode == Mode.Asleep) WakeUp();
                else if (_mode == Mode.Resting) _restUntil = DateTime.UtcNow;  // start the first pace on the next tick
            }
            else
            {
                // Work done: a party hat and a dance at the next rest, then outings as usual
                _celebrate = true;
                _nextOuting = DateTime.UtcNow + TimeSpan.FromSeconds(_random.Next(6, 14));
                if (_mode == Mode.Resting) _restUntil = DateTime.UtcNow;
            }
        }
    }
    private bool _busy;
    private bool _celebrate;

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
        // The first arrange of the sprite can be from before this control had its size (seen in the Context window: the
        // crab drew low and off the right edge until its first frame change); arrange it again once the window is up.
        Dispatcher.BeginInvoke(Layout, DispatcherPriority.Loaded);
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
        _face = null;
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

    /// <summary>Holds one expression for <paramref name="ms"/>, standing still, then <paramref name="then"/> (or a rest).</summary>
    private void Hold(Face face, int ms, Action? then = null)
    {
        _clip = new Sheet([Clips["faces"].Frames[(int)face]], TimeSpan.FromMilliseconds(ms));
        _face = face.ToString().ToLowerInvariant();
        _mode = Mode.Acting;
        _then = then;
        _loop = false;
        _reverse = false;
        _target = null;
        _movesOn = _ => false;
        _frame = 0;
        _timer.Interval = _clip.FrameTime;
        Show(_clip.Frames[0]);
    }

    /// <summary>The face being held, in lower case ("wink"), for the sprite's UI Automation status; null otherwise.</summary>
    private string? _face;

    private void Rest()
    {
        _face = null;
        _mode = Mode.Resting;
        _target = null;
        _timer.Interval = Clips["idle"].FrameTime;
        Show(Clips["idle"].Frames[0]);
        // Busy, it only pauses for a breath between paces
        _restUntil = DateTime.UtcNow + TimeSpan.FromMilliseconds(_busy ? _random.Next(250, 700) : _random.Next(1500, 4500));
    }

    private void Step()
    {
        if (_mode == Mode.Asleep) return;

        if (_mode == Mode.Resting)
        {
            var now = DateTime.UtcNow;
            if (now < _restUntil) return;
            if (_busy) { Work(); return; }
            if (_celebrate) { _celebrate = false; Hold(Face.Party, 1600, () => Play("dance", Mode.Acting)); return; }
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

    /// <summary>Something to do between rests: a walk, a roll along the header, a little show on the spot, or a face.</summary>
    private void GoOut()
    {
        var pick = _active ? 0.6 + 0.4 * _random.NextDouble() : _random.NextDouble();  // active: only tricks and faces on the spot
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
            Play("roll", Mode.Acting, Dizzy, target: Math.Clamp(target, Leftmost, Rightmost), movesOn: f => f is >= 4 and < 11);
        }
        else if (pick < 0.82)
        {
            var trick = new[] { "pinch", "wave", "dance", "roll" }[_random.Next(4)];
            Play(trick, Mode.Acting, trick == "roll" ? Dizzy : null);
        }
        else
        {
            Hold(IdleFaces[_random.Next(IdleFaces.Length)], _random.Next(1100, 1700));
        }
    }

    /// <summary>A roll leaves the crab seeing stars for a moment.</summary>
    private void Dizzy() => Hold(Face.Dizzy, 900);

    /// <summary>
    /// Busy: pace the strip end to end, with a pinch at each turn; with no strip to pace, pinch and dance on the spot.
    /// </summary>
    private void Work()
    {
        // A walk ends in Rest(), which comes straight back here: so pace, pinch, pace, pinch...
        _pinchNext = !_pinchNext;
        if (_pinchNext || !HasRoom)
        {
            Play(HasRoom || _random.NextDouble() < 0.7 ? "pinch" : "dance", Mode.Acting);
            return;
        }
        var mid = (Leftmost + Rightmost) / 2;
        Play("scuttle", Mode.Walking, loop: true, target: _x > mid ? Leftmost : Rightmost);
    }
    private bool _pinchNext;

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
