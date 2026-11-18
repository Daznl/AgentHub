using System.Windows.Threading;
using AgentHub.Services;

namespace AgentHub.Controls;

/// <summary>The services' timer on the UI thread: a plain <see cref="DispatcherTimer"/>, as they had before they moved out of the app.</summary>
public sealed class DispatcherTickTimer : ITickTimer
{
    private readonly DispatcherTimer _timer;

    private DispatcherTickTimer(TimeSpan interval)
    {
        _timer = new DispatcherTimer { Interval = interval };
        _timer.Tick += (_, e) => Tick?.Invoke(this, e);
    }

    public static ITickTimer Create(TimeSpan interval) => new DispatcherTickTimer(interval);

    public event EventHandler? Tick;
    public void Start() => _timer.Start();
    public void Stop() => _timer.Stop();
}
