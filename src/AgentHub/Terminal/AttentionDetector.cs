namespace AgentHub.Terminal;

/// <summary>
/// Decides when an interactive coding-CLI session (Claude Code, Codex, Antigravity) has stopped
/// working and is waiting for the user. Pure logic with injected timestamps so it is unit-testable.
///
/// Signals used:
///   1. Terminal bell (0x07) in the output stream: raise immediately.
///   2. Input-idle: after the user's last keystroke the CLI produced a sustained burst of output
///      (its spinner/progress redraws) lasting at least <see cref="MinimumWork"/>, and then went
///      silent for <see cref="IdleThreshold"/>. That is the shape of "agent finished or is asking".
///
/// Output that arrives within <see cref="EchoGrace"/> of a keystroke is treated as echo/redraw of
/// the user's own typing and does not start a work burst, so typing a long prompt never triggers.
///
/// Attention is raised at most once per wait: once raised it stays raised until the user types,
/// which re-arms the detector for the next turn.
/// </summary>
public sealed class AttentionDetector
{
    /// <summary>Silence after a work burst before the session is considered waiting.</summary>
    public TimeSpan IdleThreshold { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>Minimum span of continuous output required to count as the agent working.</summary>
    public TimeSpan MinimumWork { get; set; } = TimeSpan.FromSeconds(1.5);

    /// <summary>Output this soon after a keystroke is the terminal echoing the user, not the agent working.</summary>
    public TimeSpan EchoGrace { get; set; } = TimeSpan.FromMilliseconds(750);

    private DateTimeOffset? _lastInputAt;
    private DateTimeOffset? _workStartedAt;
    private DateTimeOffset? _lastOutputAt;
    private bool _bell;
    private bool _notified;
    private ParseState _parse;

    /// <summary>True from the moment attention was raised until the user next types.</summary>
    public bool IsWaiting => _notified;

    /// <summary>
    /// True while the agent is producing a work burst (non-echo output within the idle threshold)
    /// and has not yet been declared waiting. Drives the "working" pulse on the pane.
    /// </summary>
    public bool IsWorking(DateTimeOffset now) =>
        !_notified
        && _workStartedAt is not null
        && _lastOutputAt is not null
        && now - _lastOutputAt.Value < IdleThreshold;

    public void Reset()
    {
        _lastInputAt = null;
        _workStartedAt = null;
        _lastOutputAt = null;
        _bell = false;
        _notified = false;
        _parse = ParseState.Normal;
    }

    // BEL (0x07) is also the terminator of OSC/DCS/APC/PM/SOS control strings, which shells and CLIs
    // emit constantly to set the window title. Only a BEL outside such a string is a real bell.
    private enum ParseState { Normal, Escape, ControlString, ControlStringEscape }

    private bool ContainsRealBell(string text)
    {
        var bell = false;
        foreach (var ch in text)
        {
            switch (_parse)
            {
                case ParseState.Normal:
                    if (ch == '\x1b') _parse = ParseState.Escape;
                    else if (ch == '\a') bell = true;
                    break;

                case ParseState.Escape:
                    // ] OSC, P DCS, _ APC, ^ PM, X SOS all carry a string terminated by BEL or ESC \.
                    _parse = ch is ']' or 'P' or '_' or '^' or 'X' ? ParseState.ControlString : ParseState.Normal;
                    break;

                case ParseState.ControlString:
                    if (ch == '\a') _parse = ParseState.Normal;
                    else if (ch == '\x1b') _parse = ParseState.ControlStringEscape;
                    break;

                case ParseState.ControlStringEscape:
                    _parse = ch == '\\' ? ParseState.Normal : ParseState.ControlString;
                    break;
            }
        }
        return bell;
    }

    /// <summary>Call when the CLI process is launched. Startup output is treated like a turn of work.</summary>
    public void OnSessionStarted(DateTimeOffset now)
    {
        Reset();
        _lastInputAt = now;
    }

    /// <summary>Call for every chunk the user sends to the process. Re-arms the detector.</summary>
    public void OnUserInput(DateTimeOffset now)
    {
        _lastInputAt = now;
        _workStartedAt = null;
        _bell = false;
        _notified = false;
    }

    /// <summary>Call for every chunk the process writes to the terminal.</summary>
    public void OnOutput(string text, DateTimeOffset now)
    {
        if (ContainsRealBell(text))
        {
            _bell = true;
        }

        _lastOutputAt = now;

        if (_workStartedAt is null && (_lastInputAt is null || now - _lastInputAt.Value >= EchoGrace))
        {
            _workStartedAt = now;
        }
    }

    /// <summary>
    /// Poll periodically. Returns true exactly once per wait, at the moment attention should be raised.
    /// </summary>
    public bool Tick(DateTimeOffset now)
    {
        if (_notified)
        {
            return false;
        }

        if (_bell)
        {
            _notified = true;
            return true;
        }

        if (_workStartedAt is null || _lastOutputAt is null)
        {
            return false;
        }

        if (_lastOutputAt.Value - _workStartedAt.Value < MinimumWork)
        {
            return false;
        }

        if (now - _lastOutputAt.Value < IdleThreshold)
        {
            return false;
        }

        _notified = true;
        return true;
    }
}
