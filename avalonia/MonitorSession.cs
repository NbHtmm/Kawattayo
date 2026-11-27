using Avalonia.Threading;

namespace ShareFileWatch;

// 画面とは分離して巡回・履歴・バックグラウンド保存を管理する。状態の変更は UI スレッドで行う。
internal sealed partial class MonitorSession
{
    private readonly StateStore __store;
    private readonly AppState __state;
    private readonly DispatcherTimer __pollTimer = new();
    private bool __exiting, __hasUnsavedChanges;
    private long __saveRevision;
    private Task? __saveWorker;
    private string? __saveError;
    private DateTimeOffset? __lastPoll;

    internal MonitorSession(StateStore store)
    {
        __store = store;
        (__state, StartupWarning) = store.Load();
        __pollTimer.Tick += async (_, _) => await Scan(automatic: true);
        __progressTimer.Tick += (_, _) => RefreshProgressCells();
        __scanFlush.Tick += (_, _) => FlushScanUpdates();
    }

    internal AppState State => __state;
    internal StateStore Store => __store;
    internal string? StartupWarning { get; }
    internal Action<bool>? RowsChanged;
    internal Action? ProgressChanged;
    internal Action<List<string>>? Alerts;
    internal bool IsScanning => __activeScanRoots.Count > 0;
    internal string Status => __saveError is not null ? "保存に失敗：" + __saveError :
        __pollError ?? (__state.Paused ? "一時停止中" : IsScanning ? $"照会中 {__activeScanRoots.Count}系統" :
            __state.Items.Count == 0 ? "監視対象を追加してください" : __lastPoll is { } at ? $"最終巡回 {at:HH:mm:ss}" : "監視中");

    private bool IsDisposed => __exiting;
    private void RefreshRows(bool inboxChanged) => RowsChanged?.Invoke(inboxChanged);
    private void RefreshProgressCells() => ProgressChanged?.Invoke();
    private void UpdateStatus() => ProgressChanged?.Invoke();
    private void RefreshHistory() { }
    private void NotifyNewChanges(List<string> alerts) { if (alerts.Count > 0) Alerts?.Invoke(alerts); }
    private static string DisplayName(string path) => Path.GetFileName(Path.TrimEndingDirectorySeparator(path)) is { Length: > 0 } name ? name : path;

    internal void UpdateSettings(bool rescan = false)
    {
        Save();
        RowsChanged?.Invoke(true);
        if (rescan) StartTimer(immediate: true);
        else StartTimer();
    }

    internal void PauseOrResume()
    {
        if (__state.Paused)
        {
            __state.Paused = false;
            StartTimer(immediate: true);
        }
        else
        {
            __state.Paused = true;
            StopScans();
        }
        UpdateSettings();
        if (!__state.Paused) StartTimer(immediate: true);
    }

    private void StopScans()
    {
        __scanGeneration++;
        __pollTimer.Stop();
        ResetQuietScan();
        foreach (var scan in __runningScans) scan.Cancel();
    }

    internal async Task Close()
    {
        FlushScanUpdates();
        __exiting = true;
        StopScans();
        __scanFlush.Stop();
        __progressTimer.Stop();
        if (__hasUnsavedChanges) Save();
        if (__saveWorker is not null) await __saveWorker;
        if (__saveError is not null) throw new IOException(__saveError);
    }

    private void Save()
    {
        __hasUnsavedChanges = true;
        __saveRevision++;
        if (__saveWorker is null || __saveWorker.IsCompleted) __saveWorker = SavePendingAsync();
    }

    private async Task SavePendingAsync()
    {
        while (__hasUnsavedChanges)
        {
            var revision = __saveRevision;
            var snapshot = __state.CaptureForSave();
            __saveError = await Task.Run(() =>
            {
                try { __store.Save(snapshot); return null; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ex.Message; }
            });
            __hasUnsavedChanges = __saveError is not null || revision != __saveRevision;
            UpdateStatus();
            if (revision == __saveRevision) break;
        }
    }
}
