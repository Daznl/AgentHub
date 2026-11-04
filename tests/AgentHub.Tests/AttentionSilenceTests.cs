using AgentHub.Terminal;

namespace AgentHub.Tests;

public class AttentionSilenceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 4, 22, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Output_after_attention_restarts_the_silence_clock_even_though_still_waiting()
    {
        var d = new AttentionDetector();
        d.OnSessionStarted(T0);
        d.OnOutput("working", T0.AddSeconds(1));
        d.OnOutput("working", T0.AddSeconds(3));
        Assert.True(d.Tick(T0.AddSeconds(7)));          // turn ended
        Assert.Equal(TimeSpan.FromMinutes(2) - TimeSpan.FromSeconds(3), d.SilentFor(T0.AddMinutes(2)));

        d.OnOutput("woke up for background job", T0.AddMinutes(2));   // no user input
        Assert.True(d.IsWaiting);                        // flag alone would miss it
        Assert.Equal(TimeSpan.FromSeconds(30), d.SilentFor(T0.AddMinutes(2).AddSeconds(30)));
    }

    [Fact]
    public void Never_any_output_counts_as_silent_forever() =>
        Assert.Equal(TimeSpan.MaxValue, new AttentionDetector().SilentFor(T0));
}
