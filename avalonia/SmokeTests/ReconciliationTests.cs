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
