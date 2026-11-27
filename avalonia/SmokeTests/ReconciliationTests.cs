#if KAWATTAYO_SMOKE
namespace ShareFileWatch;

internal static class ReconciliationTests
{
    internal static async Task Run(string fixture)
    {
        var profile = Directory.CreateDirectory(Path.Combine(fixture, "reconcile-profile")).FullName;
        var root = Directory.CreateDirectory(Path.Combine(fixture, "reconcile-folder")).FullName;
        var file = Path.Combine(root, "sample.txt");
        File.WriteAllText(file, "first");
        var clock = new StepClock();
        var monitor = new MonitorSession(StateStore.BesideExecutable(profile), new ManualTimer(), new ManualTimer(), clock);
        var scans = new List<bool>();
        var hooks = new List<ObservedFileUpdate>();
        monitor.UseSyntheticFeedForTest = true;
        monitor.ScanModeForTest = scans.Add;
        monitor.HookCandidateForTest = hooks.Add;
        monitor.State.LowServerLoad = true;
        try
        {
            await monitor.Register([root]);
            await monitor.Scan(automatic: true);
            Require(scans.SequenceEqual(new[] { true }) && hooks.Count == 0, "初回は基準のみ");

            File.AppendAllText(file, " missed event");
            clock.Advance(TimeSpan.FromMinutes(14));
            await monitor.Scan(automatic: true);
            Require(scans.Count == 1, "期限までは通知負荷を優先");

            clock.Advance(TimeSpan.FromMinutes(1));
            await monitor.Scan(automatic: true);
            Require(scans.SequenceEqual(new[] { true, true }) && hooks.Count == 1 &&
                monitor.State.History.Count == 1, "通知のない更新を期限到達後の全件確認で回収");

            monitor.FeedEventForTest(file);
            await monitor.Scan(automatic: true);
            Require(scans.SequenceEqual(new[] { true, true, false }) &&
                hooks.Count == 1 && monitor.State.History.Count == 1, "遅延イベントで二重実行しない");

            File.AppendAllText(file, " watcher error");
            monitor.FailFeedForTest();
            await monitor.Scan(automatic: true);
            Require(scans.SequenceEqual(new[] { true, true, false, true }) && hooks.Count == 2,
                "明示的な購読エラーなら期限前に全件確認");
        }
        finally { await monitor.Close(); }
    }

    internal static async Task VerifySettled(string fixture)
    {
        var dir = Directory.CreateDirectory(Path.Combine(fixture, "settled-profile")).FullName;
        var root = Directory.CreateDirectory(Path.Combine(fixture, "settled-folder")).FullName;
        var file = Path.Combine(root, "sample.txt");
        File.WriteAllText(file, "first");
        var clock = new StepClock();
        var session = new MonitorSession(StateStore.BesideExecutable(dir), new ManualTimer(), new ManualTimer(), clock);
        var alerts = new List<MonitorAlert>();
        session.Alerts += current => alerts.AddRange(current);
        session.State.LowServerLoad = true;
        session.State.StableSeconds = 15;
        session.UseSyntheticFeedForTest = true;
        try
        {
            await session.Register([root]);
            await session.Scan(automatic: true);
            File.AppendAllText(file, " updated");
            session.FeedEventForTest(file);
            await session.Scan(automatic: true);
            var item = session.State.Items.Single();
            Require(item.Unread == 1 && session.WaitingNotifications == 1 && alerts.Count == 0,
                "未読だけは更新検知時に表示");
            clock.Advance(TimeSpan.FromSeconds(14));
            await session.Scan(automatic: true);
            Require(alerts.Count == 0, "低負荷モードでも安定時間より早く通知しない");
            clock.Advance(TimeSpan.FromSeconds(1));
            await session.Scan(automatic: true);
            Require(alerts.Single().Path == file && session.WaitingNotifications == 0,
                "新しい購読イベントなしでも期限後の照会で通知");
        }
        finally { await session.Close(); }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class StepClock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        internal void Advance(TimeSpan delta) => ticks += delta.Ticks;
    }

    private sealed class ManualTimer : IMonitorTimer
    {
        public TimeSpan Interval { get; set; }
        public bool IsEnabled { get; private set; }
        public event Action? Elapsed { add { } remove { } }
        public void Start() => IsEnabled = true;
        public void Stop() => IsEnabled = false;
    }
}
#endif
