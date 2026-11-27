namespace ShareFileWatch;

// 巡回の起動と結果の取り込み。表示状態とキャッシュ更新の境界をここに置く。
/// @brief 受信箱と監視のライフサイクルを管理するメイン画面。
internal sealed partial class MainWindow
{
    private int __scanGeneration; //!< 一時停止をまたいだ照会結果を破棄する世代番号。
    private string? __pollError;
    private readonly HashSet<string> __activeScanRoots = new(StringComparer.OrdinalIgnoreCase);
    private ScanPass? __latestScanPass;
    private readonly HashSet<CancellationTokenSource> __runningScans = [];
    private readonly System.Windows.Forms.Timer __progressTimer = new() { Interval = 500 };
    // Shared by all roots, UI thread only. Never wait for a slow share before checkpointing.
    private readonly System.Windows.Forms.Timer __scanFlush = new() { Interval = 150 };
    private bool __rowsPending, __inboxPending, __scanNeedsSave;
    private readonly List<string> __alertsPending = [];

    private void FlushScanUpdates()
    {
        __scanFlush.Stop();
        if (!__rowsPending || __exiting || IsDisposed) return;
        __rowsPending = false;
        if (__scanNeedsSave || __saveError is not null)
        {
            __scanNeedsSave = false;
            Save();
        }
        if (__inboxPending) RefreshHistory();
        RefreshRows(__inboxPending);
        __inboxPending = false;
        NotifyNewChanges(__alertsPending);
        __alertsPending.Clear();
    }

    // テストから共有先の応答を止められる。通常時は同期 Win32 API をワーカーで実行する。
    private Func<string, ProbeResult>? __testFileProbe = null;

    private void StartTimer(bool immediate = false)
    {
        __pollTimer.Stop();
        if (__testMode || __exiting || __state.Paused || __state.Items.Count == 0)
        {
            return;
        }

        __pollTimer.Interval = immediate ? 50 : __state.PollSeconds * 1000;
        __pollTimer.Start();
    }

    /// @brief 空いている共有先だけ巡回。応答待ちの共有先は次の周期でも重複実行しない。
    private Task Scan(bool automatic = false)
    {
        __pollTimer.Stop();
        if (__exiting || __state.Paused || __state.Items.Count == 0)
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

        var pass = __latestScanPass = new ScanPass();
        __lastAlertCount = 0;
        __pollError = null;
        UpdateStatus();
        int generation = __scanGeneration;
        return Task.WhenAll(groups.Select(group => ScanRoot(group.Key, group, generation, pass)));
    }

    private async Task ScanRoot(string root, IEnumerable<ScanTarget> targets, int generation, ScanPass pass)
    {
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var stop = new CancellationTokenSource();
        __runningScans.Add(stop);
        __progressTimer.Start();
        try
        {
            foreach (var target in targets)
            {
                if (!CanApplyScan(generation))
                {
                    break;
                }

                var feed = target.Feed;
                var item = __state.Items.Find(i => i.Id == target.Id);
                if (item == null) continue;
                item.QueryStart = Environment.TickCount64;
                try
                {
                if (feed != null) await feed.Start(Environment.TickCount64);
                if (!CanApplyScan(generation)) break;
                long stamp = feed?.Stamp(target.IsFolder,target.Path) ?? 0;
                string[]? paths = null;
                var baseline = item.Inventory;
                if (target.IsFolder && baseline != null && feed is { Failed:false, Watcher:not null } &&
                    __checks.TryGetValue(target.Id,out var prior) && prior.Reliable && prior.Feed == feed &&
                    prior.Epoch == feed.Epoch && prior.Revision == target.Revision)
                    (stamp,paths) = feed.Snapshot(prior.Stamp);
                var priorExclusions = item.InventoryExclusions;
                var result = await Task.Run(() => ProbeTarget(target, stop.Token, baseline, paths, priorExclusions));
                if (!CanApplyScan(generation))
                {
                    break;
                }

                // 待ち時間の長い別対象を待たず、取得できた結果を UI スレッドで反映する。
                ApplyResult(result, pass, seenPaths);
                if (!target.Low && (result.Folder is { Files:null } || result.File?.State == ProbeState.Unavailable))
                    __checks.Remove(target.Id);
                if (target.Low && __state.LowServerLoad)
                {
                    bool reliable = feed is { Watcher: not null, Failed: false } &&
                        (target.IsFolder ? result.Folder?.Files != null : result.File?.State != ProbeState.Unavailable);
                    __checks[target.Id] = new(target.Revision,feed,stamp,reliable,feed?.Epoch ?? 0);
                }
                }
                finally
                {
                    item.QueryMs = Environment.TickCount64 - item.QueryStart!.Value;
                    item.QueryStart = null;
                    item.QueryStopped = stop.IsCancellationRequested;
                }
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
            if (!__exiting && !IsDisposed)
            {
                if (__runningScans.Count == 0) __progressTimer.Stop();
                FlushScanUpdates();
                RefreshProgressCells();
                UpdateStatus();
            }
        }
    }

    private bool CanApplyScan(int generation) =>
        !__exiting && !IsDisposed && !__state.Paused && generation == __scanGeneration;

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
            file = (__testFileProbe ?? FileProbe.Read)(target.Path);
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
    private sealed class ScanPass
    {
        public int AlertCount { get; set; }
    }

    private bool ApplyResult(ScanResult result, ScanPass pass, HashSet<string> seenPaths)
    {
        // 登録解除済み、または監視範囲変更前の結果を反映しない。
        var item = __state.Items.Find(x => x.Id == result.Id);
        if (item is null || item.Revision != result.Revision)
        {
            return false;
        }

        var pending = new Lazy<HashSet<string>>(PendingPaths);
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
            // 再通知が有効なら既存の未読集合を作らず、同一巡回内の重複だけを抑える。
            RecordDetectedChange(path, kind, result.At, __state.RenotifyUnread ? null : pending.Value, seenPaths, alerts);
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

        pass.AlertCount += alerts.Count;
        if (ReferenceEquals(pass, __latestScanPass))
        {
            __lastAlertCount = pass.AlertCount;
        }
        __alertsPending.AddRange(alerts);
        if (!__scanFlush.Enabled) __scanFlush.Start();
        return changed;
    }

    /// @brief 同じ巡回で重複したパスを除き、履歴と初回の未読通知に反映する。
    private void RecordDetectedChange(string path, string kind, DateTimeOffset detectedAt,
        HashSet<string>? previouslyUnread, HashSet<string> detected, List<string> alerts)
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

    /// @brief 今回通知対象になった変更を知らせる。試験中は OS 通知を抑止する。
    private void NotifyNewChanges(List<string> alerts)
    {
        // 新しい通知がない場合と無人テストでは、バルーンや通知音を出さない。
        if (alerts.Count == 0 || __testMode)
        {
            return;
        }

        // 常駐時は通知領域のバルーンに変更内容を表示する。
        if (__state.KeepInTray)
        {
            __tray.BalloonTipTitle = $"{AppDefaults.AppName} ｜ 変更を検知した {alerts.Count} ファイル";
            __tray.BalloonTipText = string.Join(Environment.NewLine, alerts.Take(3));
            __tray.ShowBalloonTip(4500);
        }
        // 常駐しない場合は、通知領域の代わりに音で変更を知らせる。
        else
        {
            System.Media.SystemSounds.Asterisk.Play();
        }
    }

    private int __lastAlertCount; //!< 直前の巡回で通知対象になったパス数。
    /// @brief 重複する登録をまとめて未読パスの集合を作る。
    private HashSet<string> PendingPaths()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in __state.Items.Where(x => x.Unread > 0))
        {
            // 個別ファイルの未読は登録パス自体を集計する。
            if (!item.IsFolder)
            {
                paths.Add(item.Path);
            }
            // フォルダ監視の未読は登録フォルダではなく変更された子パスごとに集計する。
            else
            {
                foreach (var relative in item.PendingFiles.Keys)
                {
                    paths.Add(Path.Combine(item.Path, relative));
                }
            }
        }

        return paths;
    }
}
