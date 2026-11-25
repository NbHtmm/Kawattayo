namespace ShareFileWatch;

// 巡回の起動と結果の取り込み。表示状態とキャッシュ更新の境界をここに置く。
/// @brief 受信箱と監視のライフサイクルを管理するメイン画面。
internal sealed partial class MainWindow
{
    private int __scanGeneration; //!< 巡回開始後に範囲や監視状態が変わったかを判別する世代番号。
    private string? __pollError; //!< ステータス欄に表示する直近の失敗理由。

    /// @brief 現在の設定で次の巡回を予約する。
    private void StartTimer(bool immediate = false)
    {
        __pollTimer.Stop();
        // 自動巡回を行わない状態では、次のタイマーを予約しない。
        if (__testMode || __exiting || __isScanning || __state.Paused || __state.Items.Count == 0)
        {
            return;
        }

        __pollTimer.Interval = immediate ? 50 : __state.PollSeconds * 1000;
        __pollTimer.Start();
    }

    /// @brief 一回の巡回で監視対象を照会して結果をまとめる。
    private async Task Scan()
    {
        __pollTimer.Stop();
        // 終了中・照会中・停止中・対象なしの場合は手動要求でも巡回を開始しない。
        if (__exiting || __isScanning || __state.Paused || __state.Items.Count == 0)
        {
            return;
        }

        bool inboxChanged = false;
        int generation = __scanGeneration;
        __isScanning = true;
        __pollError = null;
        UpdateStatus();
        var targets = __state.Items.Select(x => (x.Id, x.Path, x.IsFolder, x.Recursive, x.Revision)).ToArray();
        try
        {
            // SMBへの同時照会を増やさず、全対象を順に調べる。Windows UIは待たせない。
            var results = await Task.Run(() => targets.Select(x => new ScanResult(
                x.Id, x.Revision, x.IsFolder ? null : FileProbe.Read(x.Path),
                x.IsFolder ? FolderProbe.Read(x.Path, x.Recursive) : null, DateTimeOffset.Now)).ToArray());
            // 照会中に終了・停止・世代変更が起きた場合は、古い結果を採用しない。
            if (__exiting || IsDisposed || __state.Paused || generation != __scanGeneration)
            {
                return;
            }

            inboxChanged = ApplyResults(results);
        }
        catch (Exception ex)
        {
            // 画面がまだ有効な場合だけ巡回エラーをステータス表示用に残す。
            if (!__exiting && !IsDisposed)
            {
                __pollError = "照会に失敗：" + ex.Message;
            }
        }
        finally
        {
            __isScanning = false;
            // 終了中でなければ表示を更新し、次の巡回を予約する。
            if (!__exiting && !IsDisposed)
            {
                RefreshRows(inboxChanged);
                StartTimer(immediate: generation != __scanGeneration);
            }
        }
    }

    /// @brief 一回の巡回で監視対象を照会して結果をまとめる。
    private sealed record ScanResult(Guid Id, int Revision, ProbeResult? File, FolderResult? Folder, DateTimeOffset At);

    /// @brief 有効な観測結果だけを未読状態・履歴・通知へ反映する。
    private bool ApplyResults(IEnumerable<ScanResult> results)
    {
        // 差分がない巡回では未読一覧を走査しない。最初の差分を登録する前に一度だけ取得する。
        var pending = new Lazy<HashSet<string>>(PendingPaths);
        var detected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var alerts = new List<string>();
        bool baselineChanged = false;
        var byId = __state.Items.ToDictionary(x => x.Id);
        foreach (var result in results)
        {
            // 登録解除済み、または監視範囲変更前の結果は対象に適用しない。
            if (!byId.TryGetValue(result.Id, out var item) || item.Revision != result.Revision)
            {
                continue;
            }

            bool hadBaseline = item.Baseline is not null;
            DateTimeOffset? lastWrite = item.LatestFileWriteAt;
            Action<string, string> record = (path, kind) => RecordDetectedChange(path, kind, result.At, pending.Value, detected, alerts);
            // フォルダの照会結果は子ファイルの一覧として比較する。
            if (result.Folder is not null)
            {
                item.ObserveFolder(result.Folder, result.At, record);
            }
            // 個別ファイルの照会結果はそのファイルの指紋と比較する。
            else
            {
                item.Observe(result.File!, result.At, record);
            }

            baselineChanged |= (!hadBaseline && item.Baseline is not null) || item.LatestFileWriteAt != lastWrite;
        }
        __lastPoll = DateTimeOffset.Now;
        // 照会時刻と一時的な通信エラーは画面だけ更新する。
        // 比較基準・検知内容に変化があった場合や、未保存の変更がある場合だけ保存する。
        if (baselineChanged || detected.Count > 0 || __hasUnsavedChanges)
        {
            Save();
        }

        // 新しい変更を記録した場合だけ履歴画面を更新する。
        if (detected.Count > 0)
        {
            RefreshHistory();
        }

        NotifyNewChanges(alerts);
        return detected.Count > 0;
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
        __lastAlertCount = alerts.Count;
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
