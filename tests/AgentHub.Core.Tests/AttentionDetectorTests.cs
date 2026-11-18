using AgentHub.Terminal;
using Xunit;

namespace AgentHub.Tests;

public class AttentionDetectorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 10, 9, 0, 0, TimeSpan.Zero);

    private static AttentionDetector NewDetector() => new()
    {
        IdleThreshold = TimeSpan.FromSeconds(3),
        MinimumWork = TimeSpan.FromSeconds(1.5),
        EchoGrace = TimeSpan.FromMilliseconds(750)
    };

    private static DateTimeOffset At(double seconds) => T0.AddSeconds(seconds);

    /// <summary>User presses Enter, the agent spins for a while, then goes quiet.</summary>
    private static void SimulateTurn(AttentionDetector d, double inputAt, double workFrom, double workUntil)
    {
        d.OnUserInput(At(inputAt));
        for (var t = workFrom; t <= workUntil; t += 0.1)
        {
            d.OnOutput("⠋ working...", At(t));
        }
    }

    [Fact]
    public void WorkBurstThenSilence_RaisesExactlyOnce()
    {
        var d = NewDetector();
        d.OnSessionStarted(At(0));
        SimulateTurn(d, inputAt: 1, workFrom: 1.2, workUntil: 6);

        Assert.False(d.Tick(At(7)));      // only 1s of silence
        Assert.True(d.Tick(At(9.5)));     // 3.5s of silence after a 4.8s burst
        Assert.True(d.IsWaiting);
        Assert.False(d.Tick(At(20)));     // never raised twice for the same wait
        Assert.False(d.Tick(At(60)));
    }

    [Fact]
    public void KeystrokeEcho_DoesNotRaise()
    {
        var d = NewDetector();
        d.OnSessionStarted(At(0));

        // Typing a long prompt: each key is echoed within a few ms, with pauses between keys.
        for (var t = 1.0; t < 12; t += 0.4)
        {
            d.OnUserInput(At(t));
            d.OnOutput("a", At(t + 0.02));
        }

        Assert.False(d.Tick(At(20)));
        Assert.False(d.IsWaiting);
    }

    [Fact]
    public void ShortBurst_DoesNotRaise()
    {
        var d = NewDetector();
        d.OnSessionStarted(At(0));
        SimulateTurn(d, inputAt: 1, workFrom: 1.8, workUntil: 2.4); // 0.6s of output

        Assert.False(d.Tick(At(10)));
    }

    [Fact]
    public void Bell_RaisesImmediately()
    {
        var d = NewDetector();
        d.OnSessionStarted(At(0));
        d.OnUserInput(At(1));
        d.OnOutput("done\a", At(1.1));

        Assert.True(d.Tick(At(1.2)));
        Assert.False(d.Tick(At(1.7)));
    }

    [Fact]
    public void IsWorking_TrueDuringBurst_FalseWhileTypingAndAfterWaiting()
    {
        var d = NewDetector();
        d.OnSessionStarted(At(0));

        d.OnUserInput(At(1));
        d.OnOutput("a", At(1.02));                 // echo of the keystroke
        Assert.False(d.IsWorking(At(1.1)));

        SimulateTurn(d, inputAt: 2, workFrom: 2.2, workUntil: 6);
        Assert.True(d.IsWorking(At(4)));           // mid-burst
        Assert.True(d.IsWorking(At(7)));           // silent 1s, still inside the idle threshold
        Assert.False(d.IsWorking(At(9.5)));        // silent 3.5s: no longer working

        Assert.True(d.Tick(At(9.5)));              // ...and now waiting
        Assert.False(d.IsWorking(At(9.6)));
    }

    [Fact]
    public void OscTitleTerminatedByBel_IsNotABell()
    {
        var d = NewDetector();
        d.OnSessionStarted(At(0));
        d.OnUserInput(At(1));

        // PowerShell / Claude Code set the window title with ESC ] 0 ; title BEL.
        d.OnOutput("\x1b]0;Windows PowerShell\a", At(1.1));
        // Same sequence split across two chunks, and an ST-terminated variant.
        d.OnOutput("\x1b]2;Claude", At(1.2));
        d.OnOutput(" Code\a", At(1.3));
        d.OnOutput("\x1b]0;codex\x1b\\", At(1.4));

        Assert.False(d.Tick(At(1.5)));
        Assert.False(d.Tick(At(2.5)));

        // A real bell after the control strings still counts.
        d.OnOutput("\a", At(3));
        Assert.True(d.Tick(At(3.1)));
    }

    [Fact]
    public void UserInput_ClearsWaitingAndReArmsForNextTurn()
    {
        var d = NewDetector();
        d.OnSessionStarted(At(0));
        SimulateTurn(d, inputAt: 1, workFrom: 1.2, workUntil: 6);
        Assert.True(d.Tick(At(10)));
        Assert.True(d.IsWaiting);

        // User replies; the agent works again and stops again.
        SimulateTurn(d, inputAt: 11, workFrom: 11.2, workUntil: 15);
        Assert.False(d.IsWaiting);
        Assert.False(d.Tick(At(16)));
        Assert.True(d.Tick(At(19)));
    }

    [Fact]
    public void OutputBeforeAnyInput_CountsAsWork()
    {
        // Session started but detector fed output without OnSessionStarted (defensive path).
        var d = NewDetector();
        for (var t = 0.0; t <= 3; t += 0.1)
        {
            d.OnOutput("banner", At(t));
        }

        Assert.True(d.Tick(At(7)));
    }
}
