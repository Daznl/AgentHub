using AgentHub.Services;

namespace AgentHub.Tests;

/// <summary>The timer the services poll on when the Windows app does not hand them its UI-thread one.</summary>
public class TickTimerTests
{
    [Fact]
    public async Task Without_a_context_it_ticks_on_its_interval_until_stopped()
    {
        using var timer = new SynchronizationContextTickTimer(TimeSpan.FromMilliseconds(20));
        var ticks = 0;
        timer.Tick += (_, _) => Interlocked.Increment(ref ticks);
        timer.Start();
        await Until(() => Volatile.Read(ref ticks) >= 3);

        timer.Stop();
        await Task.Delay(60); // a tick already running when Stop came may still land
        var stoppedAt = Volatile.Read(ref ticks);
        await Task.Delay(150);
        Assert.Equal(stoppedAt, Volatile.Read(ref ticks));
    }

    [Fact]
    public async Task It_ticks_on_the_context_it_was_made_on_and_drops_a_tick_queued_before_stop()
    {
        var context = new QueueContext();
        SynchronizationContextTickTimer timer;
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(context);
        try { timer = new SynchronizationContextTickTimer(TimeSpan.FromMilliseconds(20)); }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }

        using (timer)
        {
            var ticks = 0;
            timer.Tick += (_, _) => ticks++;
            timer.Start();
            await Until(() => context.Queued >= 1);
            Assert.Equal(0, ticks); // nothing runs until the context runs it

            context.RunAll();
            Assert.True(ticks >= 1);

            await Until(() => context.Queued >= 1);
            timer.Stop();
            var before = ticks;
            context.RunAll();
            Assert.Equal(before, ticks);
        }
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The condition never came true.");
            await Task.Delay(5);
        }
    }

    /// <summary>A context that only queues what is posted to it, so the test decides when it runs.</summary>
    private sealed class QueueContext : SynchronizationContext
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<(SendOrPostCallback, object?)> _queue = new();

        public int Queued => _queue.Count;

        public override void Post(SendOrPostCallback d, object? state) => _queue.Enqueue((d, state));

        public void RunAll()
        {
            while (_queue.TryDequeue(out var item)) item.Item1(item.Item2);
        }
    }
}
