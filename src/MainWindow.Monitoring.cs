namespace ShareFileWatch;

// 巡回の起動と結果の取り込み。表示状態とキャッシュ更新の境界をここに置く。
/// @brief 受信箱と監視のライフサイクルを管理するメイン画面。
internal sealed partial class MainWindow
{
    private int __scanGeneration; //!< 一時停止をまたいだ照会結果を破棄する世代番号。
    private string? __pollError;
    private readonly HashSet<string> __activeScanRoots = new(StringComparer.OrdinalIgnoreCase);
    private ScanPass? __latestScanPass;

    // テストから共有先の応答を止められる。通常時は同期 Win32 API をワーカーで実行する。
    private Func<string, ProbeResult>? __testFileProbe;

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
    private Task Scan()
    {
        __pollTimer.Stop();
        if (__exiting || __state.Paused || __state.Items.Count == 0)
        {
            return Task.CompletedTask;
        }

        // 同じ UNC 共有（ローカル・割当ドライブはドライブ単位）への照会は直列に保つ。
        var groups = __state.Items
            .Select(x => new ScanTarget(x.Id, x.Path, x.IsFolder, x.Recursive, x.Revision))
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
        try
        {
            foreach (var target in targets)
            {
                if (!CanApplyScan(generation))
                {
                    break;
                }

                var result = await Task.Run(() => ProbeTarget(target));
                if (!CanApplyScan(generation))
                {
                    break;
                }

                // 待ち時間の長い別対象を待たず、取得できた結果を UI スレッドで反映する。
                ApplyResult(result, pass, seenPaths);
            }
        }
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
            if (!__exiting && !IsDisposed)
            {
                UpdateStatus();
            }
        }
    }

    private bool CanApplyScan(int generation) =>
        !__exiting && !IsDisposed && !__state.Paused && generation == __scanGeneration;

    private ScanResult ProbeTarget(ScanTarget target)
    {
        FolderResult? folder = null;
        ProbeResult? file = null;
        if (target.IsFolder)
        {
            folder = FolderProbe.Read(target.Path, target.Recursive, __store.IsInternalFile);
        }
        else if (__store.IsInternalFile(target.Path))
        {
            file = new(ProbeState.Unavailable, Error: "アプリ自身のキャッシュは監視できません");
        }
        else
        {
            file = (__testFileProbe ?? FileProbe.Read)(target.Path);
        }
        return new(target.Id, target.Revision, file, folder, DateTimeOffset.Now);
    }

    private sealed record ScanTarget(Guid Id, string Path, bool IsFolder, bool Recursive, int Revision);
    private sealed record ScanResult(Guid Id, int Revision, ProbeResult? File, FolderResult? Folder, DateTimeOffset At);
    private sealed class ScanPass
    {
        public int AlertCount { get; set; }
    }

    private void ApplyResult(ScanResult result, ScanPass pass, HashSet<string> seenPaths)
    {
        // 登録解除済み、または監視範囲変更前の結果を反映しない。
        var item = __state.Items.Find(x => x.Id == result.Id);
        if (item is null || item.Revision != result.Revision)
        {
            return;
        }

        var pending = new Lazy<HashSet<string>>(PendingPaths);
        var alerts = new List<string>();
        bool changed = false;
        var baselineBefore = item.Baseline;
        var lastWriteBefore = item.LatestFileWriteAt;
        void Record(string path, string kind)
        {
            changed = true;
            RecordDetectedChange(path, kind, result.At, pending.Value, seenPaths, alerts);
        }

        if (result.Folder is { } folder)
        {
            item.ObserveFolder(folder, result.At, Record);
        }
        else
        {
            item.Observe(result.File!, result.At, Record);
        }

        __lastPoll = result.At;
        if (changed || item.Baseline != baselineBefore || item.LatestFileWriteAt != lastWriteBefore || __hasUnsavedChanges)
        {
            Save();
        }
        if (changed)
        {
            RefreshHistory();
        }

        pass.AlertCount += alerts.Count;
        if (ReferenceEquals(pass, __latestScanPass))
        {
            __lastAlertCount = pass.AlertCount;
        }
        NotifyNewChanges(alerts);
        RefreshRows(changed);
    }

    /// @brief 同じ巡回で重複したパスを除き、履歴と初回の未読通知に反映する。
    private void RecordDetectedChange(string path, string kind, DateTimeOffset detectedAt,
        HashSet<string> previouslyUnread, HashSet<string> detected, List<string> alerts)
    {
        // 同一巡回で重複して検知したパスを履歴や通知に二重計上しない。
        if (!detected.Add(path))
        {
            return;
        }

        // 巡回前から未読だったパスは履歴に残すが、再通知はしない。
        if (!previouslyUnread.Contains(path))
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

    /// @brief 初めて未読になったパスだけを通知し、試験中は OS 通知を抑止する。
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
            __tray.BalloonTipTitle = $"{AppDefaults.AppName} ｜ 新しい未読 {alerts.Count} ファイル";
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
