using ShareFileWatch;
using Xunit;

public sealed class MonitorSessionPollingTests
{
    [Fact]
    public void ReconciliationDue_UnknownFullScanRequiresReconciliationWithoutReadingTheClock()
    {
        Assert.True(MonitorSession.ReconciliationDue(null, new ClockWithoutTimestamp()));
    }

    [Fact]
    public void ReconciliationDue_UsesMonotonicElapsedTimeAndAnInclusiveCutoff()
    {
        long last = TimeSpan.FromMinutes(2).Ticks;
        long threshold = last + TimeSpan.FromMinutes(15).Ticks;

        Assert.False(MonitorSession.ReconciliationDue(last, new FixedClock(last)));
        Assert.False(MonitorSession.ReconciliationDue(last, new FixedClock(threshold - 1)));
        Assert.True(MonitorSession.ReconciliationDue(last, new FixedClock(threshold)));
        Assert.True(MonitorSession.ReconciliationDue(last, new FixedClock(threshold + 1)));
    }


    [Fact]
    public void CanApplyScan_RejectsStaleGenerationsPausedOrClosingEvenWhenOtherConditionsMatch()
    {
        Assert.True(MonitorSession.CanApplyScan(3, 3, closing: false, paused: false));
        Assert.False(MonitorSession.CanApplyScan(2, 3, closing: false, paused: false));
        Assert.False(MonitorSession.CanApplyScan(4, 3, closing: false, paused: false));
        Assert.False(MonitorSession.CanApplyScan(3, 3, closing: true, paused: false));
        Assert.False(MonitorSession.CanApplyScan(3, 3, closing: false, paused: true));
        Assert.False(MonitorSession.CanApplyScan(3, 3, closing: true, paused: true));
    }



    [Fact]
    public void SchedulePoll_DoesNotStartWhenClosingPausedOrWithoutRegistrations()
    {
        foreach (var (closing, paused, registrations) in new[]
        {
            (true, false, 1), (false, true, 1), (false, false, 0), (true, true, 0),
        })
        {
            var timer = new RecordingTimer();
            MonitorSession.SchedulePoll(timer, closing, paused, registrations, immediate: true, seconds: 17);
            Assert.Equal(new[] { "stop" }, timer.Calls);
        }
    }

    [Fact]
    public void SchedulePoll_StopsBeforeSchedulingEitherTheConfiguredOrImmediateInterval()
    {
        var timer = new RecordingTimer();
        MonitorSession.SchedulePoll(timer, closing: false, paused: false, registrations: 1, immediate: false, seconds: 17);
        Assert.Equal(TimeSpan.FromSeconds(17), timer.Interval);
        Assert.Equal(new[] { "stop", "interval", "start" }, timer.Calls);

        timer.Calls.Clear();
        MonitorSession.SchedulePoll(timer, closing: false, paused: false, registrations: 2, immediate: true, seconds: 17);
        Assert.Equal(TimeSpan.FromMilliseconds(50), timer.Interval);
        Assert.Equal(new[] { "stop", "interval", "start" }, timer.Calls);
    }

    private sealed class RecordingTimer : IMonitorTimer
    {
        private TimeSpan _interval;
        public List<string> Calls { get; } = [];
        public TimeSpan Interval
        {
            get => _interval;
            set { _interval = value; Calls.Add("interval"); }
        }
        public bool IsEnabled { get; private set; }
        public event Action? Elapsed { add { } remove { } }
        public void Start() { IsEnabled = true; Calls.Add("start"); }
        public void Stop() { IsEnabled = false; Calls.Add("stop"); }
    }


    private sealed class FixedClock(long timestamp) : TimeProvider
    {
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => timestamp;
        public override DateTimeOffset GetUtcNow() => throw new InvalidOperationException("Wall clock is irrelevant.");
    }

    private sealed class ClockWithoutTimestamp : TimeProvider
    {
        public override long GetTimestamp() => throw new InvalidOperationException("No baseline to compare.");
    }
}
