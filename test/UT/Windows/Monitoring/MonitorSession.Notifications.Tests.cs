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

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public async Task Notification_DeletionClearsPendingUpdatesAndOnlyAlertsWhenEnabled(bool isFolder, bool notifyDeleted)
    {
        var clock = new ManualClock();
        var fixture = new MonitorFixture(clock);
        fixture.State.WaitForStable = true;
        fixture.State.StableSeconds = 2;
        fixture.State.NotifyDeleted = notifyDeleted;
        fixture.State.RenotifyUnread = true;
        var target = fixture.AddFile(isFolder ? "folder" : "report.txt");
        target.IsFolder = isFolder;
        const string relative = "report.txt";
        var before = new Fingerprint(10, 1, 1);
        var after = before with { Bytes = 2 };
        target.Baseline = ProbeState.Found;
        if (isFolder) target.Inventory = new() { [relative] = before };
        else target.Fingerprint = before;

        bool deleted = false;
        fixture.Observe = (query, _) => isFolder
            ? new(query.Id, query.Revision, null, new(deleted ? new() : new() { [relative] = after }), DateTimeOffset.UnixEpoch, null)
            : new(query.Id, query.Revision, new(deleted ? ProbeState.Missing : ProbeState.Found, deleted ? null : after),
                null, DateTimeOffset.UnixEpoch, null);
        var alerts = new List<MonitorAlert>();
        fixture.Session.Alerts += batch => alerts.AddRange(batch);

        await fixture.Session.Scan();
        Assert.Equal(1, fixture.Session.WaitingNotifications);
        Assert.Empty(alerts);

        deleted = true;
        await fixture.Session.Scan();
        if (notifyDeleted)
            Assert.Equal(isFolder ? Path.Combine(target.Path, relative) : target.Path, Assert.Single(alerts).Path);
        else
            Assert.Empty(alerts);
        Assert.Equal(0, fixture.Session.WaitingNotifications);

        clock.Advance(TimeSpan.FromSeconds(2));
        await fixture.Session.Scan();
        Assert.Equal(notifyDeleted ? 1 : 0, alerts.Count);
        await fixture.Session.Close();
    }

    [Fact]
    public async Task Notification_FolderChildrenSettleIndependentlyAfterAnotherEdit()
    {
        var clock = new ManualClock();
        var fixture = new MonitorFixture(clock);
        fixture.State.WaitForStable = true;
        fixture.State.StableSeconds = 2;
        var folder = fixture.AddFile("folder");
        folder.IsFolder = true;
        var first = new Fingerprint(10, 1, 1);
        folder.Inventory = new() { ["a.txt"] = first, ["b.txt"] = first };
        ulong bSize = 2;
        fixture.Observe = (query, _) => new(query.Id, query.Revision, null,
            new(new() { ["a.txt"] = first with { Bytes = 2 }, ["b.txt"] = first with { Bytes = bSize } }),
            DateTimeOffset.UnixEpoch, null);
        var alerts = new List<MonitorAlert>();
        fixture.Session.Alerts += batch => alerts.AddRange(batch);

        await fixture.Session.Scan();
        Assert.Equal(2, fixture.Session.WaitingNotifications);
        clock.Advance(TimeSpan.FromSeconds(1));
        bSize++;
        await fixture.Session.Scan();
        Assert.Empty(alerts);
        clock.Advance(TimeSpan.FromSeconds(1));
        await fixture.Session.Scan();
        Assert.Equal(Path.Combine(folder.Path, "a.txt"), Assert.Single(alerts).Path);
        Assert.Equal(1, fixture.Session.WaitingNotifications);
        clock.Advance(TimeSpan.FromSeconds(1));
        await fixture.Session.Scan();
        Assert.Equal(new[] { Path.Combine(folder.Path, "a.txt"), Path.Combine(folder.Path, "b.txt") },
            alerts.Select(alert => alert.Path));
        Assert.Equal(0, fixture.Session.WaitingNotifications);
        await fixture.Session.Close();
    }


    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Notification_DirectProbeOutageRestartsStabilityWithoutAFalseDeletion(bool renotify, bool changedDuringOutage)
    {
        const string outage = "fixture offline";
        var clock = new ManualClock();
        var fixture = new MonitorFixture(clock);
        fixture.State.WaitForStable = true;
        fixture.State.StableSeconds = 2;
        fixture.State.NotifyDeleted = true;
        fixture.State.RenotifyUnread = renotify;
        var file = fixture.AddFile("report.txt");
        var before = new Fingerprint(10, 1, 1);
        var waiting = before with { Bytes = 2 };
        file.Baseline = ProbeState.Found;
        file.Fingerprint = before;
        ProbeResult observation = new(ProbeState.Found, waiting);
        int reads = 0;
        fixture.Observe = (query, _) =>
        {
            reads++;
            return new(query.Id, query.Revision, observation, null, DateTimeOffset.UnixEpoch, null);
        };
        var alerts = new List<MonitorAlert>();
        fixture.Session.Alerts += batch => alerts.AddRange(batch);

        await fixture.Session.Scan();
        Assert.Equal(1, fixture.Session.WaitingNotifications);
        Assert.Single(fixture.State.History);
        Assert.Empty(alerts);

        clock.Advance(TimeSpan.FromSeconds(2));
        observation = new(ProbeState.Unavailable, Error: outage);
        await fixture.Session.Scan();
        Assert.True(fixture.Poll.IsEnabled);
        Assert.Equal(outage, file.Error);
        Assert.Equal(ProbeState.Found, file.Baseline);
        Assert.Equal(waiting, file.Fingerprint);
        Assert.Equal(1, file.Unread);
        Assert.Single(fixture.State.History);
        Assert.Equal(1, fixture.Session.WaitingNotifications);
        Assert.Empty(alerts);

        clock.Advance(TimeSpan.FromSeconds(9));
        var recovered = changedDuringOutage ? waiting with { Bytes = 3 } : waiting;
        observation = new(ProbeState.Found, recovered);
        await fixture.Session.Scan();
        Assert.Equal(3, reads);
        Assert.Null(file.Error);
        Assert.Equal(recovered, file.Fingerprint);
        Assert.Equal(changedDuringOutage ? 2 : 1, fixture.State.History.Count);
        Assert.Empty(alerts);

        clock.Advance(TimeSpan.FromMilliseconds(1999));
        await fixture.Session.Scan();
        Assert.Empty(alerts);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        await fixture.Session.Scan();
        Assert.Equal(file.Path, Assert.Single(alerts).Path);
        Assert.Equal(0, fixture.Session.WaitingNotifications);
        clock.Advance(TimeSpan.FromSeconds(2));
        await fixture.Session.Scan();
        Assert.Single(alerts);
        await fixture.Session.Close();
    }


    [Fact]
    public async Task Notification_FolderMetadataChangeDoesNotWaitOnAnUnchangedChild()
    {
        var fixture = new MonitorFixture(new ManualClock());
        fixture.State.WaitForStable = true;
        fixture.State.StableSeconds = 2;
        fixture.State.WatchDirectoryTime = true;
        var folder = fixture.AddFile("folder");
        folder.IsFolder = true;
        long directoryStamp = 10;
        var child = new Fingerprint(10, 1, 1);
        fixture.Observe = (query, _) => new(query.Id, query.Revision, null,
            new(new() { ["report.txt"] = child }) { DirectoryStamp = directoryStamp }, DateTimeOffset.UnixEpoch, null);
        var alerts = new List<MonitorAlert>();
        fixture.Session.Alerts += batch => alerts.AddRange(batch);

        await fixture.Session.Scan();
        Assert.Empty(alerts);
        directoryStamp++;
        await fixture.Session.Scan();
        Assert.Equal(folder.Path, Assert.Single(alerts).Path);
        Assert.Equal(0, fixture.Session.WaitingNotifications);
        Assert.Equal(1, folder.Unread);
        await fixture.Session.Scan();
        Assert.Single(alerts);
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
