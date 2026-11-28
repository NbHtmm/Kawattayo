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
