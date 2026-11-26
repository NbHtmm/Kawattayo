using System.Text.Json.Serialization;

namespace ShareFileWatch;

// 1件の監視状態。通信エラーと、最後に成功した比較基準を別々に持つ。
/// @brief 監視対象一件の基準値・未読・直近の観測結果。
internal sealed class WatchItem
{
    public Guid Id { get; set; } = Guid.NewGuid(); //!< 登録を識別する ID または対象の絶対パス。
    public string Path { get; set; } = ""; //!< 監視対象の絶対パス。
    public string DisplayName { get; set; } = ""; //!< 空なら実際のファイル・フォルダ名。
    public bool IsFolder
    {
        get; set;
    } //!< フォルダ種別および配下を再帰監視する設定。
    public bool Recursive
    {
        get; set;
    } //!< フォルダ種別および配下を再帰監視する設定。
    public int Revision
    {
        get; set;
    } //!< 範囲変更前の観測結果を捨てるための世代番号。
    // フォルダの比較基準。同じフォルダでも監視範囲を変えたときは新しい基準を作る。
    public string[]? InventoryExclusions { get; set; } //!< 最後に成功した一覧を作った除外ルール。
    public Dictionary<string, Fingerprint>? Inventory
    {
        get; set;
    } //!< 前回正常に取得した比較基準。
    public ProbeState? Baseline
    {
        get; set;
    } //!< 前回正常に取得した比較基準。
    public Fingerprint? Fingerprint
    {
        get; set;
    } //!< ファイル本体を読まずに前回と比較する時刻・サイズの組。
    [JsonIgnore]
    public DateTimeOffset? LastChecked
    {
        get; set;
    } //!< 照会・変更・既読・実ファイル更新の各時刻。
    public DateTimeOffset? LastChanged
    {
        get; set;
    } //!< 照会・変更・既読・実ファイル更新の各時刻。
    public DateTimeOffset? AcknowledgedAt
    {
        get; set;
    } //!< 照会・変更・既読・実ファイル更新の各時刻。
    public DateTimeOffset? LatestFileWriteAt
    {
        get; set;
    } //!< 照会・変更・既読・実ファイル更新の各時刻。
    public DateTimeOffset? LastModified
    {
        get; set;
    } //!< 照会・変更・既読・実ファイル更新の各時刻。
    public Dictionary<string, FileChange> PendingFiles { get; set; } = new(StringComparer.OrdinalIgnoreCase); //!< 未読の子ファイルまたは最後に検知した変化。
    public string LastChange { get; set; } = ""; //!< 未読の子ファイルまたは最後に検知した変化。
    public int Unread
    {
        get; set;
    } //!< 未確認の変更件数、またはそれを示すテーマ色。
    [JsonIgnore]
    public string? Error
    {
        get; set;
    } //!< 取得できなかった理由。正常なときは null。

    /// @brief 前回成功時の基準と比較し、通信断を削除と区別する。
    public string? Observe(ProbeResult result, DateTimeOffset now, Action<string, string>? onChange = null)
    {
        LastChecked = now;
        // 照会不能は消失と区別し、最後に成功した比較基準を保持する。
        if (result.State == ProbeState.Unavailable)
        {
            Error = result.Error ?? "確認不可";
            return null;
        }
        Error = null;
        var change = DetectChange(result);
        // 変更があったときだけ、その変更に対応する実ファイルの更新時刻を見直す。
        if (change is not null)
        {
            LastModified = change == "更新" && result.Fingerprint is { } version && version.Modified != Fingerprint?.Modified
                ? FileChange.GetModifiedTime(version.Modified, now) : null;
        }

        Baseline = result.State;
        Fingerprint = result.Fingerprint;
        LatestFileWriteAt = result.Fingerprint is { } present ? FileChange.GetModifiedTime(present.Modified, now) : LatestFileWriteAt;
        // 基準に差分があるときだけ、新しい履歴と未読状態を記録する。
        if (change is not null)
        {
            onChange?.Invoke(Path, change);
            RecordChange(change, 1, now);
        }
        return change;
    }

    /// @brief 前回成功時の基準と比較し、通信断を削除と区別する。
    public string? ObserveFolder(FolderResult result, DateTimeOffset now, Action<string, string>? onChange = null)
    {
        LastChecked = now;
        // フォルダを列挙できない場合は前回一覧を残し、子ファイルの消失にしない。
        if (result.Files is null)
        {
            Error = result.Error ?? "フォルダを確認できません";
            return null;
        }
        Error = null;
        Baseline = ProbeState.Found;
        // 初回照会または監視範囲変更後は、現在の一覧を基準にして通知しない。
        if (Inventory is null)
        {
            ReplaceInventory(result.Files, result.Exclusions, now);
            return null;
        }

        bool scopeChanged = !FileExclusions.Same(InventoryExclusions, result.Exclusions);
        var changes = FolderProbe.Compare(Inventory, result.Files, now,
            scopeChanged ? InventoryExclusions : null, scopeChanged ? result.Exclusions : null);
        // 子ファイルに差分がなければ、未読も履歴も増やさない。
        if (changes.Count == 0)
        {
            // 旧版から開いたときなど、表示用の時刻だけ補う。
            // 旧設定などで表示用の日時だけが欠けていれば、既存ファイルから補う。
            if (scopeChanged || (LatestFileWriteAt is null && result.Files.Count != 0))
            {
                ReplaceInventory(result.Files, result.Exclusions, now);
            }

            return null;
        }

        ReplaceInventory(result.Files, result.Exclusions, now);
        foreach (var change in changes)
        {
            onChange?.Invoke(System.IO.Path.Combine(Path, change.Relative), change.Kind);
            AddPending(change);
        }
        var summary = FolderProbe.Summary(changes);
        RecordChange(summary, changes.Count, now);
        return $"{changes.Count}件：{summary}";
    }

    /// @brief フォルダの比較基準と最新時刻を更新する。
    private void ReplaceInventory(Dictionary<string, Fingerprint> files, string[] exclusions, DateTimeOffset now)
    {
        Inventory = files;
        InventoryExclusions = exclusions;
        LatestFileWriteAt = files.Count == 0 ? null : FileChange.GetModifiedTime(files.Values.Max(x => x.Modified), now);
    }

    // 未読はパスごとにまとめる。最終イベントと、まだ読んでいない変化の件数を残す。
    /// @brief 同一相対パスの未読をまとめる。
    private void AddPending(FileChange change)
    {
        // すでに未読の子パスへの変更は、新しい行を増やさず同じ記録にまとめる。
        if (PendingFiles.TryGetValue(change.Relative, out var prior))
        {
            change = change with
            {
                Count = (int)Math.Min((long)prior.Count + 1, int.MaxValue)
            };
        }

        PendingFiles[change.Relative] = change;
    }

    /// @brief 旧形式から引き継いだ内訳不明の件数。
    [JsonIgnore]
    public int SavedUnreadWithoutDetails => (int)Math.Max(0, Unread - PendingFiles.Values.Sum(x => (long)x.Count));

    /// @brief 対象範囲の未読を消し既読時刻を記録する。
    public void AcknowledgeFile(FileChange expected)
    {
        // フォルダを開く間に同じファイルへ新しい変更が来たら、古い操作で消さない。
        // 操作開始時と異なる変更に置き換わっていた場合は、新しい未読を消さない。
        if (!PendingFiles.TryGetValue(expected.Relative, out var latest) || latest != expected)
        {
            return;
        }

        PendingFiles.Remove(expected.Relative);
        Unread = Math.Max(0, Unread - expected.Count);
        AcknowledgedAt = DateTimeOffset.Now;
    }

    /// @brief 範囲変更時に比較基準を作り直す。
    public void SetRecursive(bool recursive)
    {
        // 再帰設定が同じなら比較基準を破棄せず、そのまま監視を続ける。
        if (Recursive == recursive)
        {
            return;
        }

        Recursive = recursive;
        Revision++;
        Inventory = null; // 監視範囲の変更をファイルの追加・削除として扱わない。
        Baseline = null;
        Error = null;
    }

    /// @brief 変更の種類・時刻・未確認件数を記録する。
    private void RecordChange(string change, int count, DateTimeOffset now)
    {
        LastChange = change;
        LastChanged = now;
        Unread = (int)Math.Min((long)Unread + count, int.MaxValue);
    }

    /// @brief 前回と今回の観測を比較して変化の種類を返す。
    private string? DetectChange(ProbeResult result)
    {
        // 前回の基準がない初回観測を追加・消失とはみなさない。
        if (Baseline is null)
        {
            return null;
        }

        // 存在状態が変わった場合は、内容の更新とは区別して出現・消失を返す。
        if (Baseline != result.State)
        {
            return result.State == ProbeState.Found ? "出現" : "消失";
        }

        return result.State == ProbeState.Found && Fingerprint != result.Fingerprint ? "更新" : null;
    }

    /// @brief 対象範囲の未読を消し既読時刻を記録する。
    public void Acknowledge()
    {
        // すでに既読なら確認時刻や未読情報を書き換えない。
        if (Unread == 0)
        {
            return;
        }

        Unread = 0;
        PendingFiles.Clear();
        AcknowledgedAt = DateTimeOffset.Now;
    }
}

/// @brief exe と同じ場所に保存するアプリ全体の状態。
internal sealed class AppState
{
    public Queue<HistoryEntry> History { get; set; } = new(); //!< 保存する履歴の上限または検知順の履歴。
    public string[] ExcludedFilePatterns { get; set; } = []; //!< 監視から外すファイル名パターン。
    public int TextSizePoints { get; set; } = 10; //!< UI の基準文字サイズ（pt）。
    public bool RenotifyUnread { get; set; } //!< 未読パスの再更新でも通知する。
    public bool ReadOnFileOpen { get; set; } = true; //!< 永続化する操作・監視・表示の設定。
    public bool? RecursiveFolders
    {
        get; set;
    } //!< 永続化する操作・監視・表示の設定。
    public bool DarkMode
    {
        get; set;
    } //!< 永続化する操作・監視・表示の設定。
    public int Version { get; set; } = 1; //!< 保存形式のバージョン。
    public int PollSeconds { get; set; } = AppDefaults.DefaultPollSeconds; //!< 監視間隔（秒）。
    public bool Paused
    {
        get; set;
    } //!< 監視一時停止・通知領域への常駐設定。
    public bool KeepInTray { get; set; } = true; //!< 監視一時停止・通知領域への常駐設定。
    public List<WatchItem> Items { get; set; } = []; //!< 登録した監視対象の一覧。
}

/// @brief 検知時刻・パス・変更種別を記録する履歴項目。
internal sealed record HistoryEntry(DateTimeOffset At, string Path, string Kind);
