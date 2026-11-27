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

    internal static async Task VerifyBatchRegistration(string fixture)
    {
        var profile = Directory.CreateDirectory(Path.Combine(fixture, "registration-profile")).FullName;
        var root = Directory.CreateDirectory(Path.Combine(fixture, "registration-source")).FullName;
        var file = Path.Combine(root, "sample.txt");
        var missing = Path.Combine(root, "not-found.txt");
        var settings = Path.Combine(profile, AppDefaults.SettingsFileName);
        File.WriteAllText(file, "baseline");
        var store = StateStore.BesideExecutable(profile);
        var session = new MonitorSession(store, new ManualTimer(), new ManualTimer());
        try
        {
            session.State.LowServerLoad = false;
            session.State.RecursiveFolders = true;
            var failures = await session.Register([root, file, file, missing, settings]);
            Require(failures.Length == 2 && failures[0].StartsWith(missing, StringComparison.Ordinal) &&
                failures[1].StartsWith(settings, StringComparison.Ordinal), "Batch registration reports both invalid paths");

            var targets = session.State.Items.ToDictionary(x => x.Path, StringComparer.OrdinalIgnoreCase);
            Require(targets.Count == 2 && targets[root] is { IsFolder: true, Recursive: true } &&
                targets[file] is { IsFolder: false, Recursive: false }, "Batch registration keeps unique valid file and folder");
            Require((await session.Register([root + Path.DirectorySeparatorChar, file])).Length == 0 &&
                session.State.Items.Select(x => x.Id).SequenceEqual(targets.Values.Select(x => x.Id)),
                "Registering the same normalized paths preserves existing target IDs");
        }
        finally { await session.Close(); }

        var restored = store.Load().State.Items;
        Require(restored.Count == 2 && restored.Select(x => x.Path).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals([root, file]),
            "Restart restores only the valid unique registrations");
    }


    internal static async Task VerifyJournalOverflow(string fixture)
    {
        var profile = Directory.CreateDirectory(Path.Combine(fixture, "overflow-profile")).FullName;
        var root = Directory.CreateDirectory(Path.Combine(fixture, "overflow-input")).FullName;
        var changed = Path.Combine(root, "changed.txt");
        File.WriteAllText(changed, "before");
        var session = new MonitorSession(StateStore.BesideExecutable(profile), new ManualTimer(), new ManualTimer(), new StepClock());
        var fullScans = new List<bool>();
        session.UseSyntheticFeedForTest = true;
        session.ScanModeForTest = fullScans.Add;
        session.State.LowServerLoad = true;
        session.State.WaitForStable = false;
        try
        {
            await session.Register([root]);
            await session.Scan(automatic: true);
            var item = session.State.Items.Single();
            Require(item.Unread == 0 && fullScans.SequenceEqual(new[] { true }), "Initial scan only captures baseline");

            File.AppendAllText(changed, " after initial scan");
            session.FeedEventForTest(changed);
            // Only the first signal represents a real change. The synthetic burst forces its history out.
            for (int index = 0; index < 4_100; ++index)
                session.FeedEventForTest(Path.Combine(root, $"unused-{index}.txt"));
            await session.Scan(automatic: true);
            Require(fullScans.SequenceEqual(new[] { true, true }), "Journal overflow forces a full scan before the periodic deadline");
            Require(item.Unread == 1 && item.PendingFiles.Count == 1 && item.PendingFiles.ContainsKey("changed.txt") &&
                session.State.History.Single().Path == changed, "Full scan recovers the lost signal without spurious files");

            await session.Scan(automatic: true);
            Require(fullScans.Count == 2 && item.Unread == 1, "A reconciled journal does not replay or re-scan on the next idle poll");
        }
        finally { await session.Close(); }
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
