namespace ShareFileWatch;

// 巡回の起動と結果の取り込み。表示状態とキャッシュ更新の境界をここに置く。
/// @brief 受信箱と監視のライフサイクルを管理するメイン画面。
internal sealed partial class MonitorSession
{
    private int __scanGeneration; //!< 一時停止をまたいだ照会結果を破棄する世代番号。
    private string? __pollError;
    private readonly HashSet<string> __activeScanRoots = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<CancellationTokenSource> __runningScans = [];
    // Shared by all roots, UI thread only. Never wait for a slow share before checkpointing.
    private bool __rowsPending, __inboxPending, __scanNeedsSave;
    private readonly List<MonitorAlert> __alertsPending = [];
#if KAWATTAYO_SMOKE
    internal Action? BeforeApplyForTest { get; set; }
    internal Action<bool>? ScanModeForTest { get; set; }
    internal Action<ObservedFileUpdate>? HookCandidateForTest { get; set; }
#endif

    private void FlushScanUpdates()
    {
        __scanFlush.Stop();
        if (!__rowsPending || __closing) return;
        __rowsPending = false;
        if (__scanNeedsSave || __writer.Error is not null)
        {
            __scanNeedsSave = false;
            __writer.Enqueue();
        }
        RowsChanged?.Invoke(__inboxPending);
        __inboxPending = false;
        if (__alertsPending.Count != 0) Alerts?.Invoke(__alertsPending);
        __alertsPending.Clear();
    }


    internal void StartTimer(bool immediate = false)
    {
        __pollTimer.Stop();
        if (__closing || __state.Paused || __state.Items.Count == 0)
        {
            return;
        }

        __pollTimer.Interval = immediate ? TimeSpan.FromMilliseconds(50) : TimeSpan.FromSeconds(__state.PollSeconds);
        __pollTimer.Start();
    }

    /// @brief 空いている共有先だけ巡回。応答待ちの共有先は次の周期でも重複実行しない。
    internal Task Scan(bool automatic = false)
    {
        __pollTimer.Stop();
        if (__closing || __state.Paused || __state.Items.Count == 0)
        {
            return Task.CompletedTask;
        }

        // 同じ UNC 共有（ローカル・割当ドライブはドライブ単位）への照会は直列に保つ。
        var exclusions = __state.ExcludedFilePatterns; // 巡回開始時のルールをワーカーに渡す。
        var low = __state.LowServerLoad && automatic;
        var feeds = low ? PrepareFeeds() : null;
        bool Due(WatchItem i)
        {
            if (__settled.HasPending(i.Id) || !low || !__checks.TryGetValue(i.Id,out var c) || !c.Reliable || c.Revision != i.Revision) return true;
            var f = feeds!.GetValueOrDefault(i.Id);
            return ReconciliationDue(c.LastFull, __clock) || f != c.Feed || f is null || !f.Available ||
                f.Epoch != c.Epoch || f.Failed || f.Stamp(i.IsFolder, i.Path) != c.Stamp;
        }
        var groups = __state.Items
            .Where(x => (x.IsFolder || !FileExclusions.Matches(x.Path, exclusions)) && Due(x))
            .Select(x => new ScanTarget(new(x.Id, x.Path, x.IsFolder, x.Recursive, x.Revision, exclusions, __state.WatchDirectoryTime), low, feeds?.GetValueOrDefault(x.Id)))
            .GroupBy(x => Path.TrimEndingDirectorySeparator(Path.GetPathRoot(x.Path)!), StringComparer.OrdinalIgnoreCase)
            .Where(x => __activeScanRoots.Add(x.Key))
            .ToArray();

        // 応答が返るのを待たずに次周期を予約する。遅い共有先以外は次周期でも巡回できる。
        StartTimer();
        if (groups.Length == 0)
        {
            return Task.CompletedTask;
        }

        __pollError = null;
        UpdateStatus();
        int generation = __scanGeneration;
        return Task.WhenAll(groups.Select(group => ScanRoot(group.Key, group, generation)));
    }

    private async Task ScanRoot(string root, IEnumerable<ScanTarget> targets, int generation)
    {
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var stop = new CancellationTokenSource();
        __runningScans.Add(stop);
        try
        {
            foreach (var target in targets)
            {
                if (!CanApplyScan(generation, __scanGeneration, __closing, __state.Paused))
                {
                    break;
                }

                await ScanTargetAsync(target, generation, stop.Token, seenPaths);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (CanApplyScan(generation, __scanGeneration, __closing, __state.Paused))
            {
                __pollError = "照会に失敗：" + ex.Message;
            }
        }
        finally
        {
            __activeScanRoots.Remove(root);
            __runningScans.Remove(stop);
            if (!__closing)
            {
                FlushScanUpdates();
                if (generation != __scanGeneration)
                {
                    RowsChanged?.Invoke(false);
                    StartTimer(immediate: true);
                }
                UpdateStatus();
            }
        }
    }

    // UI側で比較基準を取得し、Win32照会と差分作成だけをワーカーに渡す。
    private async Task ScanTargetAsync(ScanTarget target, int generation, CancellationToken cancellation, HashSet<string> seenPaths)
    {
        var item = __state.Items.Find(i => i.Id == target.Id);
        if (item is null) return;

        long started = Environment.TickCount64;
        item.QueryStart = started;
        RowsChanged?.Invoke(false);
        try
        {
            var feed = target.Feed;
            if (feed is not null) await feed.Start(Environment.TickCount64);
            if (!CanApplyScan(generation, __scanGeneration, __closing, __state.Paused)) return;

            var baseline = item.Inventory;
            var priorExclusions = item.InventoryExclusions;
            var (stamp, paths) = CaptureFeedDelta(target, baseline);
            // 低負荷モードでも通知待ちのファイルだけは、次の照会で再観測する。
            if (paths is not null && __settled.HasPending(item.Id))
                paths = paths.Concat(__settled.Paths(item.Id)).ToArray();
#if KAWATTAYO_SMOKE
            ScanModeForTest?.Invoke(paths is null);
#endif
            var result = await Task.Run(() => __probe.Read(target.Request, cancellation, baseline, paths, priorExclusions));
#if KAWATTAYO_SMOKE
            BeforeApplyForTest?.Invoke();
#endif
            if (!CanApplyScan(generation, __scanGeneration, __closing, __state.Paused)) return;

            // 監視先の一部が遅くても、取得できた結果から表示・保存に回す。
            ApplyResult(result, seenPaths);
            UpdateCheckpoint(target, result, stamp, full: paths is null);
        }
        finally
        {
            item.QueryMs = Environment.TickCount64 - started;
            item.QueryStart = null;
            item.QueryStopped = cancellation.IsCancellationRequested;
        }
    }

    private (long Stamp, string[]? Paths) CaptureFeedDelta(ScanTarget target, Dictionary<string, Fingerprint>? baseline)
    {
        var feed = target.Feed;
        if (target.IsFolder && baseline is not null && feed is { Failed: false, Available: true } &&
            __checks.TryGetValue(target.Id, out var prior) && prior.Reliable && prior.Feed == feed &&
            prior.Epoch == feed.Epoch && prior.Revision == target.Revision && !ReconciliationDue(prior.LastFull, __clock))
            return feed.Snapshot(prior.Stamp);

        return (feed?.Stamp(target.IsFolder, target.Path) ?? 0, null);
    }

    // イベントが来ないままでも、最後の全件成功から一定時間後に読み直す。
    internal static bool ReconciliationDue(long? lastFull, TimeProvider clock) =>
        lastFull is not long last || clock.GetElapsedTime(last) >= ReconcileInterval;

    private void UpdateCheckpoint(ScanTarget target, WatchObservation result, long stamp, bool full)
    {
        bool success = target.IsFolder ? result.Folder?.Files is not null : result.File?.State != ProbeState.Unavailable;
        if (!target.Low)
        {
            if (!success) __checks.Remove(target.Id);
            return;
        }

        var feed = target.Feed;
        bool reliable = success && feed is { Available: true, Failed: false };
        long? lastFull = success && full ? __clock.GetTimestamp() : __checks.GetValueOrDefault(target.Id)?.LastFull;
        __checks[target.Id] = new(target.Revision, feed, stamp, reliable, feed?.Epoch ?? 0, lastFull);
    }

    internal static bool CanApplyScan(int generation, int currentGeneration, bool closing, bool paused) =>
        !closing && !paused && generation == currentGeneration;

    private sealed record ScanTarget(WatchScanRequest Request, bool Low, ChangeFeed? Feed)
    {
        internal Guid Id => Request.Id;
        internal string Path => Request.Path;
        internal bool IsFolder => Request.IsFolder;
        internal int Revision => Request.Revision;
    }

    private void ApplyResult(WatchObservation result, HashSet<string> seenPaths)
    {
        var item = __state.Items.Find(x => x.Id == result.Id);
        var hook = item is null ? null : Hooks.Get(item.Id, item.Path);
        bool collectHookCandidates = hook is not null;
#if KAWATTAYO_SMOKE
        collectHookCandidates |= HookCandidateForTest is not null;
#endif
        if (MonitoringReducer.Apply(__state, result, seenPaths, collectHookCandidates) is not { } outcome) return;
#if KAWATTAYO_SMOKE
        if (HookCandidateForTest is { } observer)
            foreach (var file in outcome.FileUpdates) observer(file);
#endif

        if (hook is not null)
            foreach (var file in outcome.FileUpdates)
                Hooks.Enqueue(result.Id, hook, file.Path, file.Expected);

        __lastPoll = result.At;
        __scanNeedsSave |= outcome.NeedsSave;
        __rowsPending = true;
        __inboxPending |= outcome.UnreadChanged;
        DeliverAlerts(item!, result, outcome.Alerts);
        if (!__scanFlush.IsEnabled) __scanFlush.Start();
    }
}
