namespace ShareFileWatch;

// SQLite にある監視専用の状態。チェックポイント操作からこのテーブル群を触らない。
internal sealed class MonitoringCache(string database)
{
    private readonly Dictionary<Guid, CachedTarget> __savedTargets = [];
    private HistoryEntry[] __savedHistory = [];
    internal void Restore(AppState state)
    {
        __savedTargets.Clear();
        __savedHistory = [];
        if (!File.Exists(database)) return;

        using var db = new InventoryDb(database, false);
        if (!db.HasWatchCache()) return; // 旧い比較専用 SQLite は読み込まない。
        db.Exec("BEGIN");
        var byId = state.Items.ToDictionary(i => i.Id);
        foreach (var target in db.ReadTargets())
        {
            if (!__savedTargets.TryAdd(target.Id, target)) throw new IOException("監視キャッシュの ID が重複しています。");
            if (!byId.TryGetValue(target.Id, out var item) || !target.MatchesRegistration(item)) continue;
            db.ReadObservations(target, item);
            ValidateObservations(item);
            // 次回保存時は DB 内の状態と比較する。監視範囲不一致で読み飛ばした一覧は再利用しない。
            if (item.Recursive == target.Recursive)
                __savedTargets[item.Id] = target with { Inventory = item.Inventory, Pending = new(item.PendingFiles) };
        }

        __savedHistory = db.ReadHistory();
        state.History = new(__savedHistory);
        db.Exec("COMMIT");
    }

    internal static void ValidateObservations(WatchItem item)
    {
        if (item.Unread < 0 || item.Inventory?.Keys.Any(key => !IsRelativeEntry(key)) == true ||
            item.PendingFiles.Any(entry => (!item.IsFolder || entry.Key != ".") && !IsRelativeEntry(entry.Key)))
            throw new IOException("キャッシュの子パスまたは未読件数が無効です。");
    }

    // 比較 DB の値も、シェルを開く前に監視先の相対パスであることを確認する。
    internal static bool IsRelativeEntry(string path) => !Path.IsPathRooted(path) &&
        path.Split('\\', '/').All(part => part.Length > 0 && part is not "." and not ".." &&
            part.IndexOfAny(Path.GetInvalidFileNameChars()) < 0);

    internal void Save(AppState state)
    {
        var currentIds = state.Items.Select(item => item.Id).ToHashSet();
        var removed = __savedTargets.Keys.Where(id => !currentIds.Contains(id)).ToArray();
        var changed = state.Items.Where(item => !__savedTargets.TryGetValue(item.Id, out var saved) || !saved.Matches(item)).ToArray();
        bool historyChanged = !state.History.SequenceEqual(__savedHistory);
        if (removed.Length == 0 && changed.Length == 0 && !historyChanged) return;
        var committed = new List<CachedTarget>(changed.Length);
        bool inventoryChanged = removed.Length != 0;
        using (var db = new InventoryDb(database, true))
        {
            db.Exec("BEGIN IMMEDIATE");
            db.CreateWatchCache();
            foreach (var id in removed) db.RemoveTarget(id);
            foreach (var item in changed)
            {
                __savedTargets.TryGetValue(item.Id, out var prior);
                string? id = null;
                if (item.Inventory is not null)
                {
                    id = prior?.SnapshotId is { } existing && ReferenceEquals(prior.Inventory, item.Inventory)
                        ? existing : db.Write(item.Inventory);
                }
                inventoryChanged |= id != prior?.SnapshotId;
                var target = CachedTarget.Capture(item, id);
                db.WriteTarget(target, prior);
                committed.Add(target);
            }
            if (historyChanged) db.WriteHistory(state.History);
            if (inventoryChanged) db.Prune(db.ReferencedSnapshots());
            db.Exec("COMMIT");
        }

        // SQLite のコミットが成功して初めてメモリ上の保存済み基準を更新する。
        foreach (var id in removed) __savedTargets.Remove(id);
        foreach (var target in committed) __savedTargets[target.Id] = target;
        if (historyChanged) __savedHistory = state.History.ToArray();
    }
}
