using System.Text.Json;

namespace ShareFileWatch;

/// @brief 設定ファイルと観測キャッシュを独立に読み書きする。
internal sealed class StateStore(string file, string? database = null)
{
    private readonly string __backup = file + ".bak"; // 以前の版で作成済みのものを救済にだけ使う。
    private readonly string __temporary = file + ".tmp";
    private readonly string __database = database ?? Path.ChangeExtension(file, ".sqlite3");
    private readonly HashSet<string> __internalFiles = InternalFiles(file, database ?? Path.ChangeExtension(file, ".sqlite3"));
    private readonly Dictionary<Guid, CachedTarget> __savedTargets = [];
    private HistoryEntry[] __savedHistory = [];
    private string? __savedSettings;

    private static HashSet<string> InternalFiles(string file, string db)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { file, file + ".bak", file + ".tmp" };
        foreach (var suffix in new[] { "", "-journal", "-wal", "-shm" }) paths.Add(db + suffix);
        // 旧版を同じフォルダに置いたままでも、そのキャッシュを通常の変更として通知しない。
        if (Path.GetFileName(file) == AppDefaults.SettingsFileName)
        {
            var old = Path.Combine(Path.GetDirectoryName(file)!, "cache.json");
            foreach (var suffix in new[] { "", ".bak", ".tmp" }) paths.Add(old + suffix);
        }
        return paths;
    }

    internal string StorageDirectory => Path.GetDirectoryName(__database) ?? AppContext.BaseDirectory;

    internal bool IsInternalFile(string path)
    {
        var name = Path.GetFullPath(path);
        if (__internalFiles.Contains(name)) return true;
        string prefix = file + ".broken-";
        return name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            Guid.TryParseExact(name.AsSpan(prefix.Length), "N", out _);
    }

    internal static StateStore BesideExecutable(string directory)
    {
        var paths = new WindowsAppPaths(directory);
        var file = paths.Settings;
        var store = new StateStore(file, paths.Cache);
        if (!File.Exists(file) && !File.Exists(file + ".bak"))
        {
            store.WriteSettings(store.SerializeSettings(new()));
        }
        return store;
    }

    public (AppState State, string? Warning) Load()
    {
        var (state, warning) = LoadSettings();
        // DB の異常では設定をロールバックしない。壊れたキャッシュは上書きせずエラーを伝える。
        LoadCache(state);
        return (state, warning);
    }

    private (AppState State, string? Warning) LoadSettings()
    {
        if (!File.Exists(file) && !File.Exists(__backup)) return (new(), null);
        try
        {
            var state = ReadSettings(file);
            __savedSettings = SerializeSettings(state);
            return (state, null);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return RecoverSettings(ex);
        }
    }

    private (AppState State, string Warning) RecoverSettings(Exception primaryError)
    {
        AppState state;
        try { state = ReadSettings(__backup); }
        catch (Exception backupError) when (backupError is IOException or JsonException or UnauthorizedAccessException)
        {
            throw new IOException("設定とバックアップを読み込めません。元のファイルは上書きしていません。",
                new AggregateException(primaryError, backupError));
        }
        if (File.Exists(file)) File.Move(file, file + ".broken-" + Guid.NewGuid().ToString("N"));
        WriteSettings(SerializeSettings(state));
        return (state, "メインの設定を読み込めなかったため、バックアップから復元しました。");
    }

    private static AppState ReadSettings(string path)
    {
        using var stream = File.OpenRead(path);
        var data = JsonSerializer.Deserialize(stream, SettingsJsonContext.Default.AppState) ?? throw new JsonException();
        if (data.Version != 1) throw new NotSupportedException("この設定ファイルのバージョンには対応していません。");
        if (data.Items is null || data.Items.Any(i => i is null || i.Id == Guid.Empty || string.IsNullOrWhiteSpace(i.Path)) ||
            data.Items.Select(i => i.Id).Distinct().Count() != data.Items.Count)
            throw new JsonException("監視リストが壊れています。");

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in data.Items)
        {
            try { item.Path = item.IsFolder ? FileProbe.NormalizeFolder(item.Path) : FileProbe.Normalize(item.Path); }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
            { throw new JsonException("監視対象のパスが無効です。", ex); }
            if (!paths.Add(item.Path)) throw new JsonException("監視対象のパスが重複しています。");
        }
        data.RecursiveFolders ??= true;
        data.ExcludedFilePatterns = FileExclusions.Normalize(data.ExcludedFilePatterns);
        data.PollSeconds = Math.Clamp(data.PollSeconds, AppDefaults.MinimumPollSeconds, AppDefaults.MaximumPollSeconds);
        return data;
    }

    private void LoadCache(AppState state)
    {
        __savedTargets.Clear();
        __savedHistory = [];
        if (!File.Exists(__database)) return;

        using var db = new InventoryDb(__database, false);
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

    private static void ValidateObservations(WatchItem item)
    {
        if (item.Unread < 0 || item.Inventory?.Keys.Any(key => !IsRelativeEntry(key)) == true ||
            item.PendingFiles.Any(entry => (!item.IsFolder || entry.Key != ".") && !IsRelativeEntry(entry.Key)))
            throw new IOException("キャッシュの子パスまたは未読件数が無効です。");
    }

    // 比較 DB の値も、シェルを開く前に監視先の相対パスであることを確認する。
    private static bool IsRelativeEntry(string path) => !Path.IsPathRooted(path) &&
        path.Split('\\', '/').All(part => part.Length > 0 && part is not "." and not ".." &&
            part.IndexOfAny(Path.GetInvalidFileNameChars()) < 0);

    // 設定が変わらない巡回では JSON を書き直さない。観測はすべて SQLite の一括更新で確定する。
    public void Save(AppState state)
    {
        var settings = SerializeSettings(state);
        if (settings != __savedSettings || !File.Exists(file)) WriteSettings(settings);
        SaveCache(state);
    }

    private string SerializeSettings(AppState state) => JsonSerializer.Serialize(state, SettingsJsonContext.Default.AppState);

    private void WriteSettings(string settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        try
        {
            using (var stream = new FileStream(__temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(System.Text.Encoding.UTF8.GetBytes(settings));
                stream.Flush(flushToDisk: true);
            }
            // 書き換えは従来通り一括で行うが、新しいバックアップは作らない。
            if (File.Exists(file)) File.Replace(__temporary, file, null, true);
            else File.Move(__temporary, file);
            __savedSettings = settings;
        }
        finally
        {
            if (File.Exists(__temporary)) File.Delete(__temporary);
        }
    }

    private void SaveCache(AppState state)
    {
        var currentIds = state.Items.Select(item => item.Id).ToHashSet();
        var removed = __savedTargets.Keys.Where(id => !currentIds.Contains(id)).ToArray();
        var changed = state.Items.Where(item => !__savedTargets.TryGetValue(item.Id, out var saved) || !saved.Matches(item)).ToArray();
        bool historyChanged = !state.History.SequenceEqual(__savedHistory);
        if (removed.Length == 0 && changed.Length == 0 && !historyChanged) return;
        var committed = new List<CachedTarget>(changed.Length);
        bool inventoryChanged = removed.Length != 0;
        using (var db = new InventoryDb(__database, true))
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
