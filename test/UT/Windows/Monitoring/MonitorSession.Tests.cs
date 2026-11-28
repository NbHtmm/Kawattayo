using ShareFileWatch;
using Xunit;

public sealed class MonitorSessionTests
{
    [Fact]
    public async Task Session_UsesLoadedStateSurfacesWarningsAndOwnsTimerAndHookLifetime()
    {
        var fixture = new MonitorFixture();
        Assert.Same(fixture.State, fixture.Session.State);
        Assert.Same(fixture.Hooks, fixture.Session.Hooks);
        Assert.Equal("load warning", fixture.Session.StartupWarning);
        Assert.Equal("監視対象を追加してください", fixture.Session.Status);
        Assert.Equal(TimeSpan.FromMilliseconds(150), fixture.Flush.Interval);
        Assert.True(fixture.Poll.HasHandler);
        Assert.True(fixture.Flush.HasHandler);
        await fixture.Session.Close();
        Assert.False(fixture.Poll.IsEnabled);
        Assert.False(fixture.Flush.IsEnabled);
        Assert.True(fixture.Hooks.Disposed);
    }

    [Fact]
    public async Task Settings_RescanSchedulesImmediatelyAndOrdinaryChangesPreserveAnExistingSchedule()
    {
        var fixture = new MonitorFixture();
        fixture.AddFile("sample.txt");
        var rows = new List<bool>();
        fixture.Session.RowsChanged += rows.Add;
        fixture.Session.UpdateSettings();
        Assert.True(fixture.Poll.IsEnabled);
        int starts = fixture.Poll.Starts;
        fixture.Session.UpdateSettings();
        Assert.Equal(starts, fixture.Poll.Starts);
        fixture.Session.UpdateSettings(rescan: true);
        Assert.Equal(starts + 1, fixture.Poll.Starts);
        Assert.Equal(TimeSpan.FromMilliseconds(50), fixture.Poll.Interval);
        fixture.Session.ApplyPollInterval(45);
        Assert.Equal(45, fixture.Session.State.PollSeconds);
        Assert.Equal(TimeSpan.FromSeconds(45), fixture.Poll.Interval);
        Assert.Equal(new[] { true, true, true, true }, rows);
        await fixture.Session.Close();
        Assert.Equal(45, fixture.Saves.Last().PollSeconds);
    }

    [Fact]
    public async Task PauseOrResume_StopsPollingUntilResumedAndPersistsBothStates()
    {
        var fixture = new MonitorFixture();
        fixture.AddFile("sample.txt");
        fixture.Session.StartTimer();
        Assert.True(fixture.Poll.IsEnabled);
        fixture.Session.PauseOrResume();
        Assert.True(fixture.Session.State.Paused);
        Assert.Equal("一時停止中", fixture.Session.Status);
        Assert.False(fixture.Poll.IsEnabled);
        fixture.Session.PauseOrResume();
        Assert.False(fixture.Session.State.Paused);
        Assert.True(fixture.Poll.IsEnabled);
        Assert.Equal(TimeSpan.FromMilliseconds(50), fixture.Poll.Interval);
        await fixture.Session.Close();
        Assert.True(fixture.Saves.First().Paused);
        Assert.False(fixture.Saves.Last().Paused);
    }

    [Fact]
    public async Task Close_FailureLeavesHooksAvailableAndRestartsPollingUntilSuccessfulRetry()
    {
        var fixture = new MonitorFixture();
        fixture.AddFile("sample.txt");
        fixture.SaveError = new IOException("fixture failed");
        fixture.Session.UpdateSettings();
        Assert.Equal("fixture failed", (await Assert.ThrowsAsync<IOException>(fixture.Session.Close)).Message);
        Assert.False(fixture.Hooks.Disposed);
        Assert.True(fixture.Poll.IsEnabled);
        Assert.Equal(TimeSpan.FromMilliseconds(50), fixture.Poll.Interval);
        Assert.Contains("fixture failed", fixture.Session.Status);
        fixture.SaveError = null;
        await fixture.Session.Close();
        Assert.True(fixture.Hooks.Disposed);
        Assert.False(fixture.Poll.IsEnabled);
        Assert.Single(fixture.Saves);
    }

    [Fact]
    public void DescribeStatus_PreservesSaveAndPollErrorsAboveNormalActivity()
    {
        var at = new DateTimeOffset(2026, 1, 5, 8, 9, 7, TimeSpan.FromHours(9));
        Assert.Equal("保存に失敗：書込不可", MonitorSession.DescribeStatus("書込不可", "照会失敗", true, 2, 10, at));
        Assert.Equal("照会失敗", MonitorSession.DescribeStatus(null, "照会失敗", true, 2, 10, at));
        Assert.Equal("一時停止中", MonitorSession.DescribeStatus(null, null, true, 2, 10, at));
    }

    [Fact]
    public void DescribeStatus_DistinguishesActiveEmptyWaitingAndCompletedMonitoring()
    {
        var at = new DateTimeOffset(2026, 1, 5, 8, 9, 7, TimeSpan.FromHours(9));
        Assert.Equal("照会中 2系統", MonitorSession.DescribeStatus(null, null, false, 2, 10, at));
        Assert.Equal("監視対象を追加してください", MonitorSession.DescribeStatus(null, null, false, 0, 0, at));
        Assert.Equal("監視中", MonitorSession.DescribeStatus(null, null, false, 0, 3, null));
        Assert.Equal("最終巡回 08:09:07", MonitorSession.DescribeStatus(null, null, false, 0, 3, at));
    }
}

// Each fixture owns an in-memory profile. A missing fake throws instead of touching Windows or a shared path.
internal sealed class MonitorFixture
{
    internal const string Root = @"C:\fixture";
    internal AppState State { get; } = new() { LowServerLoad = false };
    internal Timer Poll { get; } = new();
    internal Timer Flush { get; } = new();
    internal HookSpy Hooks { get; } = new();
    internal System.Collections.Concurrent.ConcurrentQueue<AppState> Saves { get; } = new();
    internal MonitorSession Session { get; }
    internal Exception? SaveError { get; set; }
    internal Func<string, bool> Owned { get; set; } = _ => false;
    internal Func<string, bool> DirectoryExists { get; set; } = _ => false;
    internal Func<string, bool> FileExists { get; set; } = _ => false;
    internal Func<WatchScanRequest, CancellationToken, WatchObservation> Observe { get; set; } =
        (_, _) => throw new InvalidOperationException("Unexpected observation");

    internal Func<WatchScanRequest, CancellationToken, Dictionary<string, Fingerprint>?, string[]?, WatchObservation>? ReadWithDelta { get; set; }

    internal MonitorFixture(TimeProvider? clock = null, Func<string, bool, MonitorSession.IChangeFeed>? createFeed = null)
    {
        Session = new MonitorSession(new MonitorSession.Boundaries(() => (State, "load warning"),
            snapshot => { if (SaveError is { } e) throw e; Saves.Enqueue(snapshot); },
            path => Owned(path), path => DirectoryExists(path), path => FileExists(path),
            (request, token, baseline, paths, _) => ReadWithDelta?.Invoke(request, token, baseline, paths) ?? Observe(request, token), () => Hooks, createFeed), Poll, Flush, clock);
    }

    internal WatchItem AddFile(string name)
    {
        var item = new WatchItem { Path = Path.Combine(Root, name) };
        State.Items.Add(item);
        return item;
    }

    internal sealed class Timer : IMonitorTimer
    {
        public TimeSpan Interval { get; set; }
        public bool IsEnabled { get; private set; }
        internal int Starts { get; private set; }
        internal bool HasHandler => Elapsed is not null;
        public event Action? Elapsed;
        public void Start() { Starts++; IsEnabled = true; }
        public void Stop() => IsEnabled = false;
        internal void Fire() => Elapsed?.Invoke();
    }

    internal sealed class HookSpy : IUpdateHooks
    {
        internal bool Disposed { get; private set; }
        internal List<Guid> Disabled { get; } = [];
        internal List<(Guid Id, HookRule Rule, string Path, Fingerprint Fingerprint)> Queued { get; } = [];
        internal HookRule? Rule { get; set; }
        public HookRule? Get(Guid id, string target) => Rule;
        public string Status(Guid id) => throw new InvalidOperationException();
        public void Set(Guid id, HookRule? rule) => Rule = rule;
        public void Disable(Guid id) => Disabled.Add(id);
        public void Enqueue(Guid id, HookRule rule, string path, Fingerprint expected) => Queued.Add((id, rule, path, expected));
        public void Dispose() => Disposed = true;
    }
}
