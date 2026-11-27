namespace ShareFileWatch;

// 照会結果からメモリ上の監視状態だけを更新する。通知・外部実行・保存の実施は呼び出し側に任せる。
internal static class MonitoringReducer
{
    internal static ObservationOutcome? Apply(AppState state, WatchObservation observed, HashSet<string> seenPaths, bool includeFileUpdates)
    {
        var target = state.Items.Find(x => x.Id == observed.Id);
        // 登録解除済み、または設定変更前の結果は表示にもフックにも反映しない。
        if (target is null || target.Revision != observed.Revision) return null;

        var pending = state.RenotifyUnread ? null : new Lazy<PendingPathLookup>(() => new(state.Items));
        var alerts = new List<MonitorAlert>();
        List<ObservedFileUpdate>? files = null;
        bool changed = false;
        var before = WatchBaseline.Capture(target);
        if (observed.Folder is { } folder)
            target.ObserveFolder(folder, observed.At, Record, state.NotifyDeleted, observed.PreparedChanges);
        else
            target.Observe(observed.File!, observed.At, Record, state.NotifyDeleted);

        bool persist = changed || before != WatchBaseline.Capture(target);
        return new(changed, persist, alerts, files is null ? [] : files);

        void Record(string path, string kind)
        {
            changed = true;
            bool isRootFolderEvent = target.IsFolder && Path.GetFileName(path) == ".";
            if (includeFileUpdates && !isRootFolderEvent && kind is ("追加" or "更新"))
            {
                var expected = observed.Folder?.Files?.GetValueOrDefault(Path.GetRelativePath(target.Path, path))
                    ?? observed.File?.Fingerprint;
                if (expected is not null) (files ??= []).Add(new(path, expected));
            }
            if (isRootFolderEvent) path = Path.GetDirectoryName(path)!;
            RecordHistory(state, path, kind, observed.At, pending?.Value, seenPaths, alerts);
        }
    }

    private static void RecordHistory(AppState state, string path, string kind, DateTimeOffset at, PendingPathLookup? pending,
        HashSet<string> seen, List<MonitorAlert> alerts)
    {
        // 同じ巡回・共有先の重複登録で履歴と通知を増やさない。
        if (!seen.Add(path)) return;
        if (pending?.Contains(path) != true)
        {
            string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
            alerts.Add(new(path, $"{(name.Length == 0 ? path : name)}：{kind}"));
        }
        state.History.Enqueue(new(at, path, kind));
        if (state.History.Count > AppDefaults.HistoryLimit) state.History.Dequeue();
    }

    private readonly record struct WatchBaseline(ProbeState? State, Dictionary<string,Fingerprint>? Files, long? Stamp,
        string[]? Exclusions, DateTimeOffset? LastWrite)
    {
        internal static WatchBaseline Capture(WatchItem item) =>
            new(item.Baseline, item.Inventory, item.DirectoryStamp, item.InventoryExclusions, item.LatestFileWriteAt);

        public bool Equals(WatchBaseline other) => State == other.State && ReferenceEquals(Files, other.Files) &&
            Stamp == other.Stamp && FileExclusions.Same(Exclusions, other.Exclusions ?? []) && LastWrite == other.LastWrite;

        // 比較用の一時値。除外ルールは順不同なのでハッシュには使わない。
        public override int GetHashCode() => HashCode.Combine(State, Files, Stamp, LastWrite);
    }
}
