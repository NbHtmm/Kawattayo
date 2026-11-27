namespace ShareFileWatch;

// 巡回・表示への通知・保存要求を調整する。観測の反映はUIスレッドに限定する。
internal sealed partial class MonitorSession
{
    private readonly StateStore __store;
    private readonly AppState __state;
    private readonly WindowsWatchProbe __probe;
    private readonly SnapshotWriter __writer;
    private readonly IMonitorTimer __pollTimer;
    private readonly IMonitorTimer __scanFlush;
    private bool __closing;
    private DateTimeOffset? __lastPoll;
    private readonly TimeProvider __clock;

    internal MonitorSession(StateStore store, IMonitorTimer pollTimer, IMonitorTimer flushTimer, TimeProvider? clock = null)
    {
        __store = store;
        __clock = clock ?? TimeProvider.System;
        __pollTimer = pollTimer;
        __scanFlush = flushTimer;
        __scanFlush.Interval = TimeSpan.FromMilliseconds(150);
        __probe = new(store.IsInternalFile);
        (__state, StartupWarning) = store.Load();
        Hooks = new UpdateHooks(store.StorageDirectory);
        __writer = new(__state.CaptureForSave, __store.Save, UpdateStatus);
        __pollTimer.Elapsed += async () => await Scan(automatic: true);
        __scanFlush.Elapsed += () => FlushScanUpdates();
    }

    internal AppState State => __state;
    internal UpdateHooks Hooks { get; }
    internal string? StartupWarning { get; }
    internal event Action<bool>? RowsChanged;
    internal event Action? ProgressChanged;
    internal event Action<IReadOnlyList<MonitorAlert>>? Alerts;
    private bool IsScanning => __activeScanRoots.Count > 0;
    internal string Status => __writer.Error is string error ? "保存に失敗：" + error :
        __pollError ?? (__state.Paused ? "一時停止中" : IsScanning ? $"照会中 {__activeScanRoots.Count}系統" :
            __state.Items.Count == 0 ? "監視対象を追加してください" : __lastPoll is { } at ? $"最終巡回 {at:HH:mm:ss}" : "監視中");

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
