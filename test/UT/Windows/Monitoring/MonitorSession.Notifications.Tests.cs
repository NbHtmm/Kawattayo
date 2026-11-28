using ShareFileWatch;
using Xunit;

public sealed class MonitorSessionNotificationsTests
{
    [Theory]
    [InlineData(false, null, false)]
    [InlineData(true, null, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, true)]
    public async Task Notification_UsesTheTargetOverrideAndReleasesOnlyAfterStableTime(
        bool defaultWait, bool? targetWait, bool expectedWait)
    {
        var clock = new ManualClock();
        var fixture = new MonitorFixture(clock);
        fixture.State.WaitForStable = defaultWait;
        fixture.State.StableSeconds = 7;
        var item = fixture.AddFile("report.txt");
        item.WaitForStableOverride = targetWait;
        var before = new Fingerprint(10, 1, 1);
        item.Baseline = ProbeState.Found;
        item.Fingerprint = before;
        var next = before with { Bytes = 2 };
        fixture.Observe = (query, _) => new(query.Id, query.Revision, new(ProbeState.Found, next), null, DateTimeOffset.UnixEpoch, null);
        var alerts = new List<MonitorAlert>();
        fixture.Session.Alerts += batch => alerts.AddRange(batch);
        await fixture.Session.Scan();
        Assert.Equal(1, item.Unread);
        Assert.Equal(expectedWait ? 1 : 0, fixture.Session.WaitingNotifications);
        Assert.Equal(expectedWait ? 0 : 1, alerts.Count);
        clock.Advance(TimeSpan.FromSeconds(7) - TimeSpan.FromMilliseconds(1));
        await fixture.Session.Scan();
        Assert.Equal(expectedWait ? 0 : 1, alerts.Count);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        await fixture.Session.Scan();
        Assert.Equal(item.Path, Assert.Single(alerts).Path);
        Assert.Equal(0, fixture.Session.WaitingNotifications);
        await fixture.Session.Close();
    }

    [Fact]
    public async Task Notification_AcknowledgementAndExclusionBothRemoveWaitingAlertsWithoutDeliveringThem()
    {
        var clock = new ManualClock();
        var fixture = new MonitorFixture(clock);
        var check = fixture.AddFile("open.log");
        var excluded = fixture.AddFile("temp.tmp");
        fixture.State.WaitForStable = true;
        fixture.State.StableSeconds = 2;
        var fingerprint = new Fingerprint(10, 1, 1);
        foreach (var item in new[] { check, excluded })
        {
            item.Baseline = ProbeState.Found;
            item.Fingerprint = fingerprint;
        }
        fixture.Observe = (query, _) => new(query.Id, query.Revision, new(ProbeState.Found, fingerprint with { Bytes = 2 }),
            null, DateTimeOffset.UnixEpoch, null);
        var alerts = new List<MonitorAlert>();
        fixture.Session.Alerts += batch => alerts.AddRange(batch);
        await fixture.Session.Scan();
        Assert.Equal(2, fixture.Session.WaitingNotifications);
        check.Acknowledge();
        fixture.Session.SetExclusions("*.tmp");
        Assert.Equal(1, fixture.Session.WaitingNotifications);
        clock.Advance(TimeSpan.FromSeconds(4));
        await fixture.Session.Scan();
        Assert.Empty(alerts);
        Assert.Equal(0, fixture.Session.WaitingNotifications);
        await fixture.Session.Close();
    }

    [Fact]
    public async Task Notification_FolderFileSamplesRestartAfterAnUnavailableObservation()
    {
        var clock = new ManualClock();
        var fixture = new MonitorFixture(clock);
        fixture.State.WaitForStable = true;
        fixture.State.StableSeconds = 2;
        var folder = fixture.AddFile("folder");
        folder.IsFolder = true;
        var stamp = new Fingerprint(10, 1, 1);
        folder.Inventory = new() { ["report.txt"] = stamp };
        FolderResult observation = new(new() { ["report.txt"] = stamp with { Bytes = 2 } });
        fixture.Observe = (query, _) => new(query.Id, query.Revision, null, observation, DateTimeOffset.UnixEpoch, null);
        var alerts = new List<MonitorAlert>();
        fixture.Session.Alerts += batch => alerts.AddRange(batch);
        await fixture.Session.Scan();
        Assert.Equal(1, fixture.Session.WaitingNotifications);
        var good = observation;
        observation = new(null, "fixture offline");
        clock.Advance(TimeSpan.FromSeconds(3));
        await fixture.Session.Scan();
        Assert.Empty(alerts);
        Assert.Equal(1, fixture.Session.WaitingNotifications);
        observation = good;
        await fixture.Session.Scan();
        Assert.Empty(alerts);
        clock.Advance(TimeSpan.FromSeconds(2));
        await fixture.Session.Scan();
        Assert.Equal(Path.Combine(folder.Path, "report.txt"), Assert.Single(alerts).Path);
        Assert.Equal(0, fixture.Session.WaitingNotifications);
        await fixture.Session.Close();
    }

    private sealed class ManualClock : TimeProvider
    {
        private long _time;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _time;
        internal void Advance(TimeSpan delta) => _time += delta.Ticks;
    }
}
