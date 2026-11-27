using Avalonia.Threading;
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
    private readonly DispatcherTimer __scanFlush = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private bool __rowsPending, __inboxPending, __scanNeedsSave;
    private readonly List<string> __alertsPending = [];
#if KAWATTAYO_SMOKE
    internal Action? BeforeApplyForTest { get; set; }
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
            if (!low || !__checks.TryGetValue(i.Id,out var c) || !c.Reliable || c.Revision != i.Revision) return true;
            var f = feeds!.GetValueOrDefault(i.Id);
            return f != c.Feed || f != null && (f.Epoch != c.Epoch || f.Watcher != null && (f.Failed || f.Stamp(i.IsFolder,i.Path) != c.Stamp));
        }
        var groups = __state.Items
            .Where(x => (x.IsFolder || !FileExclusions.Matches(x.Path, exclusions)) && Due(x))
            .Select(x => new ScanTarget(x.Id, x.Path, x.IsFolder, x.Recursive, x.Revision, exclusions, __state.WatchDirectoryTime, low, feeds?.GetValueOrDefault(x.Id)))
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
                if (!CanApplyScan(generation))
                {
                    break;
                }

                await ScanTargetAsync(target, generation, stop.Token, seenPaths);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (CanApplyScan(generation))
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
            if (!CanApplyScan(generation)) return;

            var baseline = item.Inventory;
            var priorExclusions = item.InventoryExclusions;
            var (stamp, paths) = CaptureFeedDelta(target, baseline);
            var result = await Task.Run(() => ProbeTarget(target, cancellation, baseline, paths, priorExclusions));
#if KAWATTAYO_SMOKE
            BeforeApplyForTest?.Invoke();
#endif
            if (!CanApplyScan(generation)) return;

            // 監視先の一部が遅くても、取得できた結果から表示・保存に回す。
            ApplyResult(result, seenPaths);
            UpdateCheckpoint(target, result, stamp);
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
        if (target.IsFolder && baseline is not null && feed is { Failed: false, Watcher: not null } &&
            __checks.TryGetValue(target.Id, out var prior) && prior.Reliable && prior.Feed == feed &&
            prior.Epoch == feed.Epoch && prior.Revision == target.Revision)
            return feed.Snapshot(prior.Stamp);

        return (feed?.Stamp(target.IsFolder, target.Path) ?? 0, null);
    }

    private void UpdateCheckpoint(ScanTarget target, ScanResult result, long stamp)
    {
        bool success = target.IsFolder ? result.Folder?.Files is not null : result.File?.State != ProbeState.Unavailable;
        if (!target.Low)
        {
            if (!success) __checks.Remove(target.Id);
            return;
        }

        var feed = target.Feed;
        bool reliable = success && feed is { Watcher: not null, Failed: false };
        __checks[target.Id] = new(target.Revision, feed, stamp, reliable, feed?.Epoch ?? 0);
    }

    private bool CanApplyScan(int generation) =>
        !__closing && !__state.Paused && generation == __scanGeneration;

    private ScanResult ProbeTarget(ScanTarget target, CancellationToken cancellation = default, Dictionary<string,Fingerprint>? baseline = null, string[]? paths = null, string[]? priorExclusions = null)
    {
        FolderResult? folder = null;
        ProbeResult? file = null;
        if (target.IsFolder)
        {
            bool Excluded(string path) => FileExclusions.Matches(path,target.Exclusions) || __store.IsInternalFile(path);
            folder = (paths != null && baseline != null ? FolderProbe.ReadChanges(target.Path,target.Recursive,baseline,paths,Excluded,cancellation)
                : FolderProbe.Read(target.Path,target.Recursive,Excluded,cancellation)) with { Exclusions = target.Exclusions };
            // Saving the app's own cache changes its parent directory's timestamp.
            if (folder.Files != null && target.WatchTime && !__store.IsInternalFile(Path.Combine(target.Path, AppDefaults.SettingsFileName)))
            {
                try { folder = folder with { DirectoryStamp = Directory.GetLastWriteTimeUtc(target.Path).ToFileTimeUtc() }; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { folder = new(null, ex.Message); }
            }
        }
        else if (__store.IsInternalFile(target.Path))
        {
            file = new(ProbeState.Unavailable, Error: "アプリ自身のキャッシュは監視できません");
        }
        else
        {
            file = FileProbe.Read(target.Path);
        }
        var at = DateTimeOffset.Now;
        List<FileChange>? prepared = null;
        if (baseline is not null && folder?.Files is { } files)
        {
            bool scopeChanged = !FileExclusions.Same(priorExclusions, target.Exclusions);
            prepared = FolderProbe.Compare(baseline, files, at,
                scopeChanged ? priorExclusions : null, scopeChanged ? target.Exclusions : null);
        }
        return new(target.Id, target.Revision, file, folder, at, prepared);
    }

    private sealed record ScanTarget(Guid Id, string Path, bool IsFolder, bool Recursive, int Revision, string[] Exclusions, bool WatchTime, bool Low = false, ChangeFeed? Feed = null);
    private sealed record ScanResult(Guid Id, int Revision, ProbeResult? File, FolderResult? Folder, DateTimeOffset At, List<FileChange>? PreparedChanges);
    private void ApplyResult(ScanResult result, HashSet<string> seenPaths)
    {
        // 登録解除済み、または監視範囲変更前の結果を反映しない。
        var item = __state.Items.Find(x => x.Id == result.Id);
        if (item is null || item.Revision != result.Revision)
        {
            return;
        }

        var pending = __state.RenotifyUnread ? null : new Lazy<PendingPathLookup>(() => new(__state.Items));
        var alerts = new List<string>();
        bool changed = false;
        var baselineBefore = item.Baseline;
        var inventoryBefore = item.Inventory;
        var stampBefore = item.DirectoryStamp;
        var inventoryExclusionsBefore = item.InventoryExclusions;
        var lastWriteBefore = item.LatestFileWriteAt;
        void Record(string path, string kind)
        {
            changed = true;
            if (Path.GetFileName(path) == ".") path = Path.GetDirectoryName(path)!;
            // 再通知が有効なら既存の未読集合を作らず、同一巡回内の重複だけを抑える。
            RecordDetectedChange(path, kind, result.At, pending?.Value, seenPaths, alerts);
        }

        if (result.Folder is { } folder)
        {
            item.ObserveFolder(folder, result.At, Record, __state.NotifyDeleted, result.PreparedChanges);
        }
        else
        {
            item.Observe(result.File!, result.At, Record, __state.NotifyDeleted);
        }

        __lastPoll = result.At;
        bool exclusionScopeChanged = !FileExclusions.Same(inventoryExclusionsBefore, item.InventoryExclusions ?? []);
        if (changed || inventoryBefore != item.Inventory || stampBefore != item.DirectoryStamp || exclusionScopeChanged || item.Baseline != baselineBefore ||
            item.LatestFileWriteAt != lastWriteBefore)
        {
            __scanNeedsSave = true;
        }
        __rowsPending = true;
        __inboxPending |= changed;

        __alertsPending.AddRange(alerts);
        if (!__scanFlush.IsEnabled) __scanFlush.Start();
    }

    /// @brief 同じ巡回で重複したパスを除き、履歴と初回の未読通知に反映する。
    private void RecordDetectedChange(string path, string kind, DateTimeOffset detectedAt,
        PendingPathLookup? previouslyUnread, HashSet<string> detected, List<string> alerts)
    {
        // 同一巡回で重複して検知したパスを履歴や通知に二重計上しない。
        if (!detected.Add(path))
        {
            return;
        }

        // 再通知を抑止する設定でも、未読・履歴の記録自体は省略しない。
        if (previouslyUnread?.Contains(path) != true)
        {
            alerts.Add($"{DisplayName(path)}：{kind}");
        }

        __state.History.Enqueue(new(detectedAt, path, kind));
        // 履歴の保持上限を超えたら、最も古い項目を取り除く。
        if (__state.History.Count > AppDefaults.HistoryLimit)
        {
            __state.History.Dequeue();
        }
    }

}
