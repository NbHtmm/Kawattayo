namespace ShareFileWatch;

// 巡回・表示への通知・保存要求を調整する。観測の反映はUIスレッドに限定する。
internal sealed partial class MonitorSession
{
    private readonly Boundaries __boundaries;
    private readonly AppState __state;
    private readonly SnapshotWriter __writer;
    private readonly IMonitorTimer __pollTimer;
    private readonly IMonitorTimer __scanFlush;
    private bool __closing;
    private DateTimeOffset? __lastPoll;
    private readonly TimeProvider __clock;
    private readonly long __clockOrigin;

    internal MonitorSession(StateStore store, IMonitorTimer pollTimer, IMonitorTimer flushTimer, TimeProvider? clock = null)
        : this(new Boundaries(store.Load, store.Save, store.IsInternalFile, Directory.Exists, File.Exists,
            new WindowsWatchProbe(store.IsInternalFile).Read, () => new UpdateHooks(store.StorageDirectory)), pollTimer, flushTimer, clock)
    {
    }

    // The session owns orchestration. Tests use substitutes for disk, native reads, and external hooks.
    internal sealed record Boundaries(Func<(AppState State, string? Warning)> Load, Action<AppState> Save,
        Func<string, bool> IsInternalFile, Func<string, bool> DirectoryExists, Func<string, bool> FileExists,
        Func<WatchScanRequest, CancellationToken, Dictionary<string, Fingerprint>?, string[]?, string[]?, WatchObservation> Read,
        Func<IUpdateHooks> CreateHooks);

    internal MonitorSession(Boundaries boundaries, IMonitorTimer pollTimer, IMonitorTimer flushTimer, TimeProvider? clock = null)
    {
        __boundaries = boundaries;
        __clock = clock ?? TimeProvider.System;
        __clockOrigin = __clock.GetTimestamp();
        __pollTimer = pollTimer;
        __scanFlush = flushTimer;
        __scanFlush.Interval = TimeSpan.FromMilliseconds(150);
        (__state, StartupWarning) = boundaries.Load();
        Hooks = boundaries.CreateHooks();
        __writer = new(__state.CaptureForSave, boundaries.Save, UpdateStatus);
        __pollTimer.Elapsed += async () => await Scan(automatic: true);
        __scanFlush.Elapsed += () => FlushScanUpdates();
    }

    internal AppState State => __state;
    internal IUpdateHooks Hooks { get; }
    internal string? StartupWarning { get; }
    internal event Action<bool>? RowsChanged;
    internal event Action? ProgressChanged;
    internal event Action<IReadOnlyList<MonitorAlert>>? Alerts;
    internal string Status =>
        DescribeStatus(__writer.Error, __pollError, __state.Paused, __activeScanRoots.Count, __state.Items.Count, __lastPoll);

    // 保護すべき保存エラーは、巡回中などの通常表示より優先する。
    internal static string DescribeStatus(string? saveError, string? pollError, bool paused, int activeRoots, int registrations, DateTimeOffset? lastPoll)
    {
        if (saveError is not null) return "保存に失敗：" + saveError;
        if (pollError is not null) return pollError;
        if (paused) return "一時停止中";
        if (activeRoots > 0) return $"照会中 {activeRoots}系統";
        if (registrations == 0) return "監視対象を追加してください";
        return lastPoll is { } at ? $"最終巡回 {at:HH:mm:ss}" : "監視中";
    }

    private void UpdateStatus() => ProgressChanged?.Invoke();

    // 監視条件の変更は、すでに待機中の照会にも適用する。旧条件で読んだ結果は捨てる。
    internal void UpdateSettings(bool rescan = false)
    {
        if (rescan) StopScans();
        __writer.Enqueue();
        RowsChanged?.Invoke(true);
        if (rescan || !__pollTimer.IsEnabled) StartTimer(immediate: rescan);
    }

    internal void ApplyPollInterval(int seconds)
    {
        __state.PollSeconds = seconds;
        UpdateSettings();
        StartTimer();
    }

    internal void PauseOrResume()
    {
        __state.Paused = !__state.Paused;
        UpdateSettings(rescan: true);
    }

    private void StopScans()
    {
        __scanGeneration++;
        __pollError = null;
        __pollTimer.Stop();
        ResetQuietScan();
        ResetPendingAlerts();
        foreach (var scan in __runningScans) scan.Cancel();
    }

    internal async Task Close()
    {
        FlushScanUpdates();
        __closing = true;
        StopScans();
        __scanFlush.Stop();
        try { await __writer.FlushAsync(); Hooks.Dispose(); }
        catch
        {
            // 保存できなければウィンドウを閉じない。修復後の再試行まで未保存の状態を残す。
            __closing = false;
            StartTimer(immediate: true);
            throw;
        }
    }
}
