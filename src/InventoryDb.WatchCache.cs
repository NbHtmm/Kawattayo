using System.Globalization;

namespace ShareFileWatch;

/// @brief SQLite に保存する観測値。ユーザー設定および照会中の情報は含まない。
internal sealed record CachedObservation(ProbeState? Baseline, Fingerprint? Fingerprint, long? DirectoryStamp,
    string Scope, DateTimeOffset? LastChanged, DateTimeOffset? AcknowledgedAt, DateTimeOffset? LatestFileWriteAt,
    DateTimeOffset? LastModified, string LastChange, int Unread)
{
    internal static CachedObservation Capture(WatchItem item) => new(item.Baseline, item.Fingerprint,
        item.DirectoryStamp, string.Join('\n', item.InventoryExclusions ?? []), item.LastChanged,
        item.AcknowledgedAt, item.LatestFileWriteAt, item.LastModified, item.LastChange, item.Unread);
}

/// @brief 監視登録と照合するための識別情報、および最後に保存した比較データ。
internal sealed record CachedTarget(Guid Id, string Path, bool IsFolder, bool Recursive, CachedObservation Observation,
    string? SnapshotId, Dictionary<string, Fingerprint>? Inventory = null, Dictionary<string, FileChange>? Pending = null)
{
    internal static CachedTarget Capture(WatchItem item, string? snapshotId) => new(item.Id, item.Path, item.IsFolder,
        item.Recursive, CachedObservation.Capture(item), snapshotId, item.Inventory, new(item.PendingFiles));

    internal bool MatchesRegistration(WatchItem item) => IsFolder == item.IsFolder &&
        string.Equals(Path, item.Path, StringComparison.OrdinalIgnoreCase);

    internal bool Matches(WatchItem item) => MatchesRegistration(item) && Recursive == item.Recursive &&
        ReferenceEquals(Inventory, item.Inventory) && Observation == CachedObservation.Capture(item) &&
        SamePending(Pending, item.PendingFiles);

    internal static bool SamePending(Dictionary<string, FileChange>? first, Dictionary<string, FileChange> second) =>
        first is not null && first.Count == second.Count &&
        first.All(entry => second.TryGetValue(entry.Key, out var value) && value == entry.Value);
}

internal sealed partial class InventoryDb
{
    internal bool HasWatchCache()
    {
        using var query = new Statement(this, "SELECT 1 FROM sqlite_master WHERE type='table' AND name='watch_state'");
        return query.Next();
    }

    internal void CreateWatchCache() => Exec("""
        CREATE TABLE IF NOT EXISTS watch_state (
            watch_id TEXT PRIMARY KEY, path TEXT NOT NULL, folder INTEGER NOT NULL, recursive INTEGER NOT NULL,
            snapshot TEXT NOT NULL, baseline INTEGER, modified INTEGER, created INTEGER, bytes INTEGER, folder_stamp INTEGER,
            exclusions TEXT NOT NULL, changed_at TEXT NOT NULL, acknowledged_at TEXT NOT NULL, latest_write_at TEXT NOT NULL,
            last_modified_at TEXT NOT NULL, last_change TEXT NOT NULL, unread INTEGER NOT NULL
        ) WITHOUT ROWID;
        CREATE TABLE IF NOT EXISTS pending_changes (
            watch_id TEXT, path TEXT, kind TEXT NOT NULL, detected_at TEXT NOT NULL, modified_at TEXT NOT NULL,
            count INTEGER NOT NULL, PRIMARY KEY (watch_id, path)
        ) WITHOUT ROWID;
        CREATE TABLE IF NOT EXISTS change_history (
            seq INTEGER PRIMARY KEY, detected_at TEXT NOT NULL, path TEXT NOT NULL, kind TEXT NOT NULL
        );
        """);

    internal List<CachedTarget> ReadTargets()
    {
        using var rows = new Statement(this, """
            SELECT watch_id,path,folder,recursive,snapshot,baseline,modified,created,bytes,folder_stamp,
                exclusions,changed_at,acknowledged_at,latest_write_at,last_modified_at,last_change,unread
            FROM watch_state
            """);
        var targets = new List<CachedTarget>();
        while (rows.Next())
        {
            if (!Guid.TryParseExact(rows.Text(0), "N", out var id)) throw new IOException("キャッシュの監視 ID が無効です。");
            long? baseline = rows.OptionalNumber(5);
            if (baseline.HasValue && baseline != (long)ProbeState.Found && baseline != (long)ProbeState.Missing)
                throw new IOException("キャッシュの観測状態が無効です。");
            var fingerprint = rows.OptionalNumber(6) is long modified
                ? new Fingerprint(modified, rows.Number(7), unchecked((ulong)rows.Number(8))) : null;
            var observation = new CachedObservation((ProbeState?)baseline, fingerprint, rows.OptionalNumber(9),
                rows.Text(10), Date(rows.Text(11)), Date(rows.Text(12)), Date(rows.Text(13)), Date(rows.Text(14)),
                rows.Text(15), Count(rows.Number(16), allowZero: true));
            var snap = rows.Text(4);
            targets.Add(new(id, rows.Text(1), rows.Number(2) != 0, rows.Number(3) != 0,
                observation, snap.Length == 0 ? null : snap));
        }
        return targets;
    }

    internal void ReadObservations(CachedTarget target, WatchItem item)
    {
        var data = target.Observation;
        item.LastChanged = data.LastChanged;
        item.AcknowledgedAt = data.AcknowledgedAt;
        item.LastModified = data.LastModified;
        item.LastChange = data.LastChange;
        item.Unread = data.Unread;
        using (var rows = new Statement(this, "SELECT path,kind,detected_at,modified_at,count FROM pending_changes WHERE watch_id=?"))
        {
            rows.Text(1, target.Id.ToString("N"));
            while (rows.Next())
            {
                var path = rows.Text(0);
                var change = new FileChange(path, rows.Text(1), RequiredDate(rows.Text(2)), Date(rows.Text(3)),
                    Count(rows.Number(4), allowZero: false));
                if (!item.PendingFiles.TryAdd(path, change)) throw new IOException("キャッシュの未読パスが重複しています。");
            }
        }
        // 設定だけが復元された場合、別の再帰範囲で作られた一覧を比較に使わない。
        if (item.Recursive != target.Recursive) return;
        item.Baseline = data.Baseline;
        item.Fingerprint = data.Fingerprint;
        item.DirectoryStamp = data.DirectoryStamp;
        item.InventoryExclusions = FileExclusions.Normalize(data.Scope.Split('\n'));
        item.LatestFileWriteAt = data.LatestFileWriteAt;
        item.Inventory = target.SnapshotId is { } id ? Read(id) : null;
    }

    internal void WriteTarget(CachedTarget target, CachedTarget? prior)
    {
        using var row = new Statement(this, """
            INSERT OR REPLACE INTO watch_state(watch_id,path,folder,recursive,snapshot,baseline,modified,created,bytes,folder_stamp,
                exclusions,changed_at,acknowledged_at,latest_write_at,last_modified_at,last_change,unread)
            VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)
            """);
        var key = target.Id.ToString("N");
        var data = target.Observation;
        row.Text(1, key);
        row.Text(2, target.Path);
        row.Number(3, target.IsFolder ? 1 : 0);
        row.Number(4, target.Recursive ? 1 : 0);
        row.Text(5, target.SnapshotId ?? "");
        row.Number(6, (long?)data.Baseline);
        row.Number(7, data.Fingerprint?.Modified);
        row.Number(8, data.Fingerprint?.Created);
        row.Number(9, data.Fingerprint is { } found ? unchecked((long)found.Bytes) : null);
        row.Number(10, data.DirectoryStamp);
        row.Text(11, data.Scope);
        row.Text(12, Date(data.LastChanged));
        row.Text(13, Date(data.AcknowledgedAt));
        row.Text(14, Date(data.LatestFileWriteAt));
        row.Text(15, Date(data.LastModified));
        row.Text(16, data.LastChange);
        row.Number(17, data.Unread);
        row.Run();
        if (!CachedTarget.SamePending(prior?.Pending, target.Pending!)) WritePending(key, target.Pending!.Values);
    }

    private void WritePending(string key, IEnumerable<FileChange> changes)
    {
        DeleteByWatchId("DELETE FROM pending_changes WHERE watch_id=?", key);
        using var row = new Statement(this, "INSERT INTO pending_changes VALUES(?,?,?,?,?,?)");
        row.Text(1, key);
        foreach (var change in changes)
        {
            row.Text(2, change.Relative);
            row.Text(3, change.Kind);
            row.Text(4, Date(change.DetectedAt));
            row.Text(5, Date(change.ModifiedAt));
            row.Number(6, change.Count);
            row.Run();
        }
    }

    internal void RemoveTarget(Guid id)
    {
        var key = id.ToString("N");
        DeleteByWatchId("DELETE FROM pending_changes WHERE watch_id=?", key);
        DeleteByWatchId("DELETE FROM watch_state WHERE watch_id=?", key);
    }

    private void DeleteByWatchId(string sql, string key)
    {
        using var row = new Statement(this, sql);
        row.Text(1, key);
        row.Run();
    }

    internal List<string> ReferencedSnapshots()
    {
        using var rows = new Statement(this, "SELECT snapshot FROM watch_state WHERE snapshot <> ''");
        var ids = new List<string>();
        while (rows.Next()) ids.Add(rows.Text(0));
        return ids;
    }

    internal HistoryEntry[] ReadHistory()
    {
        using var rows = new Statement(this, "SELECT detected_at,path,kind FROM change_history ORDER BY seq DESC LIMIT ?");
        rows.Number(1, AppDefaults.HistoryLimit);
        var history = new List<HistoryEntry>();
        while (rows.Next())
        {
            var path = rows.Text(1);
            if (!Path.IsPathFullyQualified(path)) throw new IOException("変更履歴のパスが無効です。");
            history.Add(new(RequiredDate(rows.Text(0)), path, rows.Text(2)));
        }
        history.Reverse();
        return history.ToArray();
    }

    internal void WriteHistory(IEnumerable<HistoryEntry> history)
    {
        Exec("DELETE FROM change_history");
        using var row = new Statement(this, "INSERT INTO change_history(detected_at,path,kind) VALUES(?,?,?)");
        foreach (var entry in history.TakeLast(AppDefaults.HistoryLimit))
        {
            row.Text(1, Date(entry.At));
            row.Text(2, entry.Path);
            row.Text(3, entry.Kind);
            row.Run();
        }
    }

    private static int Count(long n, bool allowZero) => n >= (allowZero ? 0 : 1) && n <= int.MaxValue
        ? (int)n : throw new IOException("キャッシュの件数が無効です。");

    private static string Date(DateTimeOffset? value) => value?.ToString("O", CultureInfo.InvariantCulture) ?? "";
    private static DateTimeOffset? Date(string value) => value.Length == 0 ? null : RequiredDate(value);
    private static DateTimeOffset RequiredDate(string value) =>
        DateTimeOffset.TryParseExact(value, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)
            ? at : throw new IOException("キャッシュの日時が無効です。");
}
