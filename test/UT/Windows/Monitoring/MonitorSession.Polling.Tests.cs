using ShareFileWatch;
using Xunit;

public sealed class MonitorSessionPollingTests
{
    [Fact]
    public async Task Scan_EmptyPausedExcludedOrClosedSessionDoesNotQuery()
    {
        var fixture = new MonitorFixture();
        await fixture.Session.Scan();
        Assert.False(fixture.Poll.IsEnabled);
        var file = fixture.AddFile("skip.tmp");
        fixture.State.Paused = true;
        await fixture.Session.Scan();
        Assert.False(fixture.Poll.IsEnabled);
        Assert.Null(file.LastChecked);
        fixture.State.Paused = false;
        fixture.State.ExcludedFilePatterns = ["*.tmp"];
        await fixture.Session.Scan();
        Assert.True(fixture.Poll.IsEnabled);
        Assert.Null(file.LastChecked);
        await fixture.Session.Close();
        await fixture.Session.Scan();
        Assert.False(fixture.Poll.IsEnabled);
        Assert.Null(file.LastChecked);
    }

    [Fact]
    public async Task Scan_FirstReadEstablishesBaselineAndSecondReadPublishesUnreadAlertAndHookOnce()
    {
        DateTimeOffset at = new(2026, 3, 1, 13, 2, 5, TimeSpan.FromHours(9));
        var fixture = new MonitorFixture();
        var file = fixture.AddFile("report.txt");
        fixture.State.WaitForStable = false;
        var hook = new HookRule(file.Path, "not started", [], 0);
        fixture.Hooks.Rule = hook;
        Fingerprint observed = new(at.ToFileTime(), 5, 2);
        fixture.Observe = (q, _) => new(q.Id, q.Revision, new(ProbeState.Found, observed), null, at, null);
        var alerts = new List<MonitorAlert>();
        var rows = new List<bool>();
        int progress = 0;
        fixture.Session.Alerts += changes => alerts.AddRange(changes);
        fixture.Session.RowsChanged += rows.Add;
        fixture.Session.ProgressChanged += () => progress++;
        await fixture.Session.Scan();
        Assert.Same(observed, file.Fingerprint);
        Assert.Empty(alerts);
        Assert.Empty(fixture.Hooks.Queued);
        Assert.Equal(0, file.Unread);
        Assert.Equal("最終巡回 13:02:05", fixture.Session.Status);

        observed = observed with { Bytes = 30 };
        await fixture.Session.Scan();
        Assert.Equal(new MonitorAlert(file.Path, "report.txt：更新"), Assert.Single(alerts));
        var queued = Assert.Single(fixture.Hooks.Queued);
        Assert.Equal((file.Id, hook, file.Path, observed), queued);
        Assert.Equal(1, file.Unread);
        Assert.Contains(true, rows);
        Assert.True(progress > 0);
        Assert.Null(file.QueryStart);
        Assert.NotNull(file.QueryMs);
        Assert.False(file.QueryStopped);
        fixture.Flush.Fire();
        Assert.Single(alerts);

        await fixture.Session.Scan();
        Assert.Single(alerts);
        Assert.Single(fixture.Hooks.Queued);
        await fixture.Session.Close();
        Assert.Equal(1, fixture.Saves.Last().Items.Single().Unread);
    }

    [Fact]
    public async Task Scan_PauseCancelsRunningReadAndStaleReturnCannotChangeBaseline()
    {
        var fixture = new MonitorFixture();
        var file = fixture.AddFile("trial.txt");
        var first = new Fingerprint(20, 1, 4);
        var latest = new Fingerprint(21, 1, 5);
        file.Baseline = ProbeState.Found;
        file.Fingerprint = first;
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim(false);
        var stop = TestContext.Current.CancellationToken;
        fixture.Observe = (q, token) =>
        {
            started.SetResult(token);
            Assert.True(release.Wait(TimeSpan.FromSeconds(5), stop), "Scan worker was not released");
            return new(q.Id, q.Revision, new(ProbeState.Found, latest), null, DateTimeOffset.UnixEpoch, null);
        };
        var pending = fixture.Session.Scan();
        try
        {
            var scanToken = await started.Task.WaitAsync(TimeSpan.FromSeconds(5), stop);
            await fixture.Session.Scan(); // The already-running root is not queued a second time.
            fixture.Session.PauseOrResume();
            Assert.True(scanToken.IsCancellationRequested);
        }
        finally { release.Set(); }
        await pending;
        Assert.Same(first, file.Fingerprint);
        Assert.Equal(0, file.Unread);
        Assert.True(file.QueryStopped);
        Assert.Null(file.QueryStart);
        Assert.False(fixture.Poll.IsEnabled);
        await fixture.Session.Close();
    }

    [Fact]
    public async Task Scan_ReportsUnexpectedProbeErrorAndAllowsTheSameRootToRecover()
    {
        var fixture = new MonitorFixture();
        var file = fixture.AddFile("trial.txt");
        fixture.Observe = (_, _) => throw new InvalidOperationException("probe fixture");
        await fixture.Session.Scan();
        Assert.Equal("照会に失敗：probe fixture", fixture.Session.Status);
        Assert.Null(file.LastChecked);
        Assert.Null(file.QueryStart);
        var now = new DateTimeOffset(2026, 1, 1, 11, 32, 49, TimeSpan.Zero);
        fixture.Observe = (q, _) => new(q.Id, q.Revision, new(ProbeState.Missing), null, now, null);
        await fixture.Session.Scan();
        Assert.Equal(now, file.LastChecked);
        Assert.Equal("最終巡回 11:32:49", fixture.Session.Status);
        await fixture.Session.Close();
    }

    [Fact]
    public void ClaimPollRoots_CaseInsensitiveDriveAndShareGroupsKeepTargetOrder()
    {
        string[] paths = [@"C:\first.txt", @"\\nas\logs\a.log", @"c:\folder\second.txt",
            @"\\NAS\LOGS\b.log", @"\\nas\results\x.csv", @"D:\other.txt"];
        var inFlight = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var groups = MonitorSession.ClaimPollRoots(paths, static x => x, inFlight);
        Assert.Equal(4, groups.Length);
        Assert.Equal(new[] { paths[0], paths[2] }, groups[0]);
        Assert.Equal(new[] { paths[1], paths[3] }, groups[1]);
        Assert.Equal(paths[4], Assert.Single(groups[2]));
        Assert.Equal(paths[5], Assert.Single(groups[3]));
        Assert.Equal(4, inFlight.Count);
    }

    [Fact]
    public void ClaimPollRoots_BusyRootIsSkippedUntilReleasedWithoutBlockingOtherRoots()
    {
        string[] paths = [@"C:\busy.txt", @"D:\ready.txt"];
        var inFlight = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"c:\" };
        Assert.Empty(MonitorSession.ClaimPollRoots(Array.Empty<string>(), static x => x, inFlight));
        Assert.Single(inFlight);
        var ready = Assert.Single(MonitorSession.ClaimPollRoots(paths, static x => x, inFlight));
        Assert.Equal(paths[1], Assert.Single(ready));
        Assert.Empty(MonitorSession.ClaimPollRoots(paths, static x => x, inFlight));
        Assert.True(inFlight.Remove(@"C:\"));
        var released = Assert.Single(MonitorSession.ClaimPollRoots(paths, static x => x, inFlight));
        Assert.Equal(paths[0], Assert.Single(released));
    }

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

    [Fact]
    public async Task Scan_ReliableQuietFolderSkipsUnchangedAndReadsOnlyDirtyPaths()
    {
        var feed = new FakeFeed();
        var fixture = FolderFixture(feed);
        var paths = RecordFolderReads(fixture);
        await fixture.Session.Scan(automatic: true);
        Assert.Null(Assert.Single(paths));
        await fixture.Session.Scan(automatic: true);
        Assert.Single(paths);

        feed.Record(@"C:\fixture\logs\report.txt");
        await fixture.Session.Scan(automatic: true);
        Assert.Equal(new[] { @"C:\fixture\logs\report.txt" }, paths[1]);
        Assert.Equal(0, feed.LastCursor);
        await fixture.Session.Scan(automatic: true);
        Assert.Equal(2, paths.Count);
        await fixture.Session.Close();
    }

    [Fact]
    public async Task Scan_QuietShareFailurePreservesInventoryAndRecoveryRequiresFullRead()
    {
        var feed = new FakeFeed();
        var fixture = FolderFixture(feed);
        var item = Assert.Single(fixture.State.Items);
        var paths = RecordFolderReads(fixture, failAt: 2);
        await fixture.Session.Scan(automatic: true);
        var baseline = item.Inventory;
        Assert.NotNull(baseline);

        feed.Record(@"C:\fixture\logs\report.txt");
        await fixture.Session.Scan(automatic: true);
        Assert.Same(baseline, item.Inventory);
        Assert.Equal(0, item.Unread);
        Assert.Equal("照会失敗（毎回照会）", fixture.Session.DescribeChangeFeed(item));

        await fixture.Session.Scan(automatic: true);
        Assert.Equal(3, paths.Count);
        Assert.Null(paths[2]);
        Assert.Equal("通知待機中", fixture.Session.DescribeChangeFeed(item));
        await fixture.Session.Scan(automatic: true);
        Assert.Equal(3, paths.Count);
        await fixture.Session.Close();
    }

    [Fact]
    public async Task Scan_ReplacedFeedAndMissingJournalAndReconciliationDeadlineUseFullReads()
    {
        var feed = new FakeFeed();
        var clock = new MutableClock();
        var fixture = FolderFixture(feed, clock);
        var paths = RecordFolderReads(fixture);
        await fixture.Session.Scan(automatic: true);
        feed.Epoch++;
        await fixture.Session.Scan(automatic: true);
        Assert.Null(paths[1]);

        feed.Record(@"C:\fixture\logs\report.txt");
        feed.LostHistory = true;
        await fixture.Session.Scan(automatic: true);
        Assert.Null(paths[2]);

        clock.Timestamp = TimeSpan.FromMinutes(15).Ticks - 1;
        await fixture.Session.Scan(automatic: true);
        Assert.Equal(3, paths.Count);
        clock.Timestamp++;
        await fixture.Session.Scan(automatic: true);
        Assert.Equal(4, paths.Count);
        Assert.All(paths, value => Assert.Null(value));
        await fixture.Session.Close();
    }

    [Fact]
    public async Task Scan_ManualFailureInvalidatesQuietBaselineButManualSuccessDoesNotAdvanceQuietCursor()
    {
        var feed = new FakeFeed();
        var fixture = FolderFixture(feed);
        var paths = RecordFolderReads(fixture, failAt: 4);
        await fixture.Session.Scan(automatic: true);
        feed.Record(@"C:\fixture\logs\report.txt");

        await fixture.Session.Scan(); // Manual scans always probe in full.
        Assert.Null(paths[1]);
        await fixture.Session.Scan(automatic: true);
        Assert.Equal(new[] { @"C:\fixture\logs\report.txt" }, paths[2]);

        await fixture.Session.Scan();
        Assert.Null(paths[3]);
        await fixture.Session.Scan(automatic: true);
        Assert.Equal(5, paths.Count);
        Assert.Null(paths[4]);
        await fixture.Session.Close();
    }

    [Fact]
    public async Task Scan_StaleQuietResultCannotConsumeEventOrPublishAChange()
    {
        var feed = new FakeFeed();
        var fixture = FolderFixture(feed);
        var file = Assert.Single(fixture.State.Items);
        var at = DateTimeOffset.UnixEpoch;
        var observed = new Fingerprint(10, 1, 10);
        bool stale = false;
        int calls = 0;
        fixture.Observe = (request, _) =>
        {
            calls++;
            return new(request.Id, request.Revision + (stale ? 1 : 0), null,
                new(new(StringComparer.OrdinalIgnoreCase) { ["report.txt"] = observed }), at, null);
        };

        await fixture.Session.Scan(automatic: true);
        observed = observed with { Bytes = 30 };
        at = at.AddSeconds(1);
        stale = true;
        feed.Record(@"C:\fixture\logs\report.txt");
        await fixture.Session.Scan(automatic: true);
        Assert.Equal(0, file.Unread);
        Assert.Equal(DateTimeOffset.UnixEpoch, file.LastChecked);
        Assert.Empty(fixture.State.History);
        Assert.Equal(10UL, file.Inventory!["report.txt"].Bytes);

        stale = false;
        await fixture.Session.Scan(automatic: true);
        Assert.Equal(3, calls);
        Assert.Equal(1, file.Unread);
        Assert.Equal(30UL, file.Inventory!["report.txt"].Bytes);
        Assert.Equal(at, file.LastChecked);
        await fixture.Session.Close();
    }

    [Fact]
    public async Task Scan_UnavailableOrFailedFeedDoesNotAllowQuietSkipping()
    {
        var feed = new FakeFeed { Available = false };
        var fixture = FolderFixture(feed);
        var paths = RecordFolderReads(fixture);
        await fixture.Session.Scan(automatic: true);
        await fixture.Session.Scan(automatic: true);
        Assert.Equal(2, paths.Count);
        Assert.Equal("利用不可（毎回照会）", fixture.Session.DescribeChangeFeed(fixture.State.Items[0]));

        feed.Available = true;
        await fixture.Session.Scan(automatic: true);
        feed.Failed = true;
        await fixture.Session.Scan(automatic: true);
        Assert.Equal(4, paths.Count);
        Assert.All(paths, value => Assert.Null(value));
        await fixture.Session.Close();
    }

    [Fact]
    public async Task Scan_ClosingDuringProbeDiscardsCancellationAndDoesNotStartAnotherTarget()
    {
        var fixture = new MonitorFixture();
        var first = fixture.AddFile("a.txt");
        var second = fixture.AddFile("b.txt");
        var begun = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stop = TestContext.Current.CancellationToken;
        using var release = new ManualResetEventSlim(false);
        fixture.Observe = (request, token) =>
        {
            Assert.Equal(first.Id, request.Id);
            begun.SetResult(token);
            Assert.True(release.Wait(TimeSpan.FromSeconds(5), stop));
            token.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Expected scan token to be canceled.");
        };
        var scan = fixture.Session.Scan();
        try
        {
            var token = await begun.Task.WaitAsync(TimeSpan.FromSeconds(5), stop);
            await fixture.Session.Close();
            Assert.True(token.IsCancellationRequested);
        }
        finally { release.Set(); }

        await scan.WaitAsync(TimeSpan.FromSeconds(5), stop);
        Assert.False(fixture.Poll.IsEnabled);
        Assert.False(fixture.Flush.IsEnabled);
        Assert.True(first.QueryStopped);
        Assert.Null(first.QueryStart);
        Assert.Null(second.QueryMs);
        Assert.Empty(fixture.State.History);
    }

    [Fact]
    public async Task Scan_FileFeedTreatsMissingAsSuccessfulAndUnavailableAsRetryable()
    {
        var feed = new FakeFeed();
        var fixture = new MonitorFixture(createFeed: (_, _) => feed);
        fixture.State.LowServerLoad = true;
        var file = fixture.AddFile("report.txt");
        int calls = 0;
        fixture.Observe = (request, _) => new(request.Id, request.Revision,
            ++calls == 2 ? new(ProbeState.Unavailable, Error: "offline") : new(ProbeState.Missing),
            null, DateTimeOffset.UnixEpoch.AddSeconds(calls), null);

        Assert.Equal("未確認", fixture.Session.DescribeChangeFeed(file));
        await fixture.Session.Scan(automatic: true);
        Assert.Equal("通知待機中", fixture.Session.DescribeChangeFeed(file));
        await fixture.Session.Scan(automatic: true);
        Assert.Equal(1, calls);
        feed.Record(file.Path);
        await fixture.Session.Scan(automatic: true);
        Assert.Equal("照会失敗（毎回照会）", fixture.Session.DescribeChangeFeed(file));
        Assert.Equal(ProbeState.Missing, file.Baseline);
        await fixture.Session.Scan(automatic: true);
        Assert.Equal(3, calls);
        Assert.Equal("通知待機中", fixture.Session.DescribeChangeFeed(file));
        await fixture.Session.Close();
    }

    [Fact]
    public async Task Scan_QuietNotificationWaitResamplesFileWithoutNewJournalEvent()
    {
        var feed = new FakeFeed();
        var clock = new MutableClock();
        var fixture = FolderFixture(feed, clock);
        fixture.State.WaitForStable = true;
        fixture.State.StableSeconds = 2;
        var observed = new Fingerprint(10, 1, 10);
        var alerts = new List<MonitorAlert>();
        var inputs = new List<string[]?>();
        fixture.Session.Alerts += values => alerts.AddRange(values);
        fixture.ReadWithDelta = (request, _, _, paths) =>
        {
            inputs.Add(paths);
            return new(request.Id, request.Revision, null, new(new(StringComparer.OrdinalIgnoreCase)
                { ["report.txt"] = observed }), DateTimeOffset.UnixEpoch, null);
        };
        await fixture.Session.Scan(automatic: true);
        observed = observed with { Bytes = 20 };
        feed.Record(@"C:\fixture\logs\report.txt");
        await fixture.Session.Scan(automatic: true);
        Assert.Equal(1, fixture.Session.WaitingNotifications);
        Assert.Empty(alerts);

        clock.Timestamp = TimeSpan.FromSeconds(2).Ticks;
        await fixture.Session.Scan(automatic: true);
        Assert.Equal(3, inputs.Count);
        Assert.Equal(new[] { @"C:\fixture\logs\report.txt" }, inputs[2]);
        Assert.Equal(@"C:\fixture\logs\report.txt", Assert.Single(alerts).Path);
        Assert.Equal(0, fixture.Session.WaitingNotifications);
        await fixture.Session.Close();
    }

    [Fact]
    public async Task Scan_ExcludedRegistrationsDoNotUseFeedSlotsAndAllocatedFeedsAreRetired()
    {
        var feeds = new List<FakeFeed>();
        var fixture = new MonitorFixture(createFeed: (_, _) =>
        {
            var feed = new FakeFeed();
            feeds.Add(feed);
            return feed;
        });
        fixture.State.LowServerLoad = true;
        fixture.State.ExcludedFilePatterns = ["*.tmp"];
        var excluded = fixture.AddFile("excluded.tmp");
        var a = fixture.AddFile("a.txt");
        var b = fixture.AddFile("b.txt");
        var directories = Enumerable.Range(0, 16).Select(i => new WatchItem
            { Path = Path.Combine(@"C:\fixture", $"folder-{i}"), IsFolder = true, Recursive = true }).ToArray();
        fixture.State.Items.AddRange(directories);
        fixture.Observe = (request, _) => new(request.Id, request.Revision, request.IsFolder ? null : new(ProbeState.Missing),
            request.IsFolder ? new(new()) : null, DateTimeOffset.UnixEpoch, null);
        await fixture.Session.Scan(automatic: true);
        Assert.Equal(16, feeds.Count);
        Assert.True(feeds[0].Names.ContainsKey(a.Path) && feeds[0].Names.ContainsKey(b.Path));
        Assert.Equal(2, feeds[0].Names.Count);
        Assert.Equal("除外", fixture.Session.DescribeChangeFeed(excluded));
        Assert.Equal("購読枠外（毎回照会）", fixture.Session.DescribeChangeFeed(directories[^1]));
        fixture.Session.RemoveTargets([a]);
        Assert.All(feeds, feed => Assert.True(feed.Retired));

        await fixture.Session.Scan(automatic: true);
        Assert.Single(feeds[16].Names);
        Assert.True(feeds[16].Names.ContainsKey(b.Path));
        fixture.State.LowServerLoad = false;
        Assert.Equal("オフ（毎回照会）", fixture.Session.DescribeChangeFeed(b));
        fixture.State.Paused = true;
        Assert.Equal("一時停止", fixture.Session.DescribeChangeFeed(b));
        await fixture.Session.Close();
        Assert.All(feeds, feed => Assert.True(feed.Retired));
    }

    [Fact]
    public async Task Scan_RescheduleSuppressesOldProbeErrorAndPrioritizesNextPoll()
    {
        var fixture = new MonitorFixture();
        var file = fixture.AddFile("report.txt");
        var stop = TestContext.Current.CancellationToken;
        var begun = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim(false);
        fixture.Observe = (_, _) =>
        {
            begun.SetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(5), stop));
            throw new IOException("obsolete share failure");
        };

        var scan = fixture.Session.Scan();
        try
        {
            await begun.Task.WaitAsync(TimeSpan.FromSeconds(5), stop);
            fixture.Session.SetExclusions("*.tmp");
        }
        finally { release.Set(); }

        await scan.WaitAsync(TimeSpan.FromSeconds(5), stop);
        Assert.Equal("監視中", fixture.Session.Status);
        Assert.Null(file.LastChecked);
        Assert.True(fixture.Poll.IsEnabled);
        Assert.Equal(TimeSpan.FromMilliseconds(50), fixture.Poll.Interval);
        await fixture.Session.Close();
    }

    [Fact]
    public async Task Scan_PausedWhileWaitingForFeedDoesNotStartProbe()
    {
        var delay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var feed = new FakeFeed { StartAsync = () => delay.Task };
        var fixture = FolderFixture(feed);
        var file = Assert.Single(fixture.State.Items);
        int calls = 0;
        fixture.Observe = (request, _) => { calls++; return new(request.Id, request.Revision, null, new(new()), DateTimeOffset.UnixEpoch, null); };

        var scan = fixture.Session.Scan(automatic: true);
        try
        {
            Assert.NotNull(file.QueryStart);
            fixture.Session.PauseOrResume();
        }
        finally { delay.SetResult(); }

        await scan.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(0, calls);
        Assert.Null(file.LastChecked);
        Assert.True(file.QueryStopped);
        Assert.True(feed.Retired);
        await fixture.Session.Close();
    }

    private static MonitorFixture FolderFixture(FakeFeed feed, TimeProvider? clock = null)
    {
        var fixture = new MonitorFixture(clock, (_, _) => feed);
        fixture.State.LowServerLoad = true;
        fixture.State.WaitForStable = false;
        fixture.State.Items.Add(new WatchItem { Path = @"C:\fixture\logs", IsFolder = true, Recursive = true });
        return fixture;
    }

    private static List<string[]?> RecordFolderReads(MonitorFixture fixture, int failAt = -1)
    {
        var inputs = new List<string[]?>();
        fixture.ReadWithDelta = (request, _, _, paths) =>
        {
            inputs.Add(paths);
            var folder = inputs.Count == failAt
                ? new FolderResult(null, "share unavailable")
                : new FolderResult(new(StringComparer.OrdinalIgnoreCase) { ["report.txt"] = new(10, 1, 10) });
            return new(request.Id, request.Revision, null, folder, DateTimeOffset.UnixEpoch.AddSeconds(inputs.Count), null);
        };
        return inputs;
    }

    private sealed class FakeFeed : MonitorSession.IChangeFeed
    {
        public System.Collections.Concurrent.ConcurrentDictionary<string, long> Names { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool Available { get; set; } = true;
        public bool Failed { get; set; }
        public int Epoch { get; set; }
        public bool LostHistory { get; set; }
        public long LastCursor { get; private set; } = -1;
        private readonly List<string> paths = [];
        public long Stamp(bool folder, string path) => folder ? paths.Count : Names.GetValueOrDefault(path);
        public (long Stamp, string[]? Paths) Snapshot(long since)
        {
            LastCursor = since;
            return (paths.Count, LostHistory ? null : paths.Skip((int)since).ToArray());
        }
        public Func<Task> StartAsync { get; init; } = () => Task.CompletedTask;
        public Task Start(long now) => StartAsync();
        public bool Retired { get; private set; }
        public void Retire() => Retired = true;
        public void Record(string path)
        {
            paths.Add(path);
            if (Names.ContainsKey(path)) Names.AddOrUpdate(path, 1, (_, current) => current + 1);
        }
    }

    private sealed class MutableClock : TimeProvider
    {
        public long Timestamp { get; set; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Timestamp;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch;
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
