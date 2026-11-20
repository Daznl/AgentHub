namespace AgentHub.Services;

/// <summary>
/// A repeating timer as the services use it, so they carry no UI types. The Windows app passes one that ticks on the UI
/// thread (<c>Controls/DispatcherTickTimer</c>, the <c>DispatcherTimer</c> these services always had); without one, a
/// service uses <see cref="SynchronizationContextTickTimer"/>.
/// </summary>
public interface ITickTimer
{
    event EventHandler? Tick;
    void Start();
    void Stop();
}

/// <summary>
/// Ticks on the synchronisation context current when it was made (the caller's own thread or loop), or on the thread
/// pool when there is none. A tick already queued when <see cref="Stop"/> is called is dropped, and once Stop returns
/// no tick starts: Stop waits for a tick that is under way on another thread.
/// </summary>
public sealed class SynchronizationContextTickTimer : ITickTimer, IDisposable
{
    private readonly TimeSpan _interval;
    private readonly SynchronizationContext? _context = SynchronizationContext.Current;
    private readonly System.Threading.Timer _timer;
    private readonly object _tickLock = new();
    private int _generation;

    public SynchronizationContextTickTimer(TimeSpan interval)
    {
        _interval = interval;
        _timer = new System.Threading.Timer(_ => Fire(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public static ITickTimer Create(TimeSpan interval) => new SynchronizationContextTickTimer(interval);

    public event EventHandler? Tick;

    public void Start() => _timer.Change(_interval, _interval);

    public void Stop()
    {
        lock (_tickLock) _generation++;
        _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    private void Fire()
    {
        int generation;
        lock (_tickLock) generation = _generation;
        void Raise()
        {
            lock (_tickLock)
            {
                if (generation == _generation) Tick?.Invoke(this, EventArgs.Empty);
            }
        }

        if (_context is null) Raise();
        else _context.Post(_ => Raise(), null);
    }

    public void Dispose()
    {
        Stop();
        _timer.Dispose();
    }
}
