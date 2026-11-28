using Avalonia.Threading;

namespace ShareFileWatch;

internal sealed class AvaloniaMonitorTimer : IMonitorTimer
{
    private readonly DispatcherTimer timer = new();

    internal AvaloniaMonitorTimer() => timer.Tick += (_, _) => Elapsed?.Invoke();
    public TimeSpan Interval { get => timer.Interval; set => timer.Interval = value; }
    public bool IsEnabled => timer.IsEnabled;
    public event Action? Elapsed;
    public void Start() => timer.Start();
    public void Stop() => timer.Stop();
}
