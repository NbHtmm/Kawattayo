using System.Text.Json;

namespace ShareFileWatch;

// 設定、未読、履歴は一緒に保存し、一時ファイルとバックアップも同じ場所に置く。
/// @brief 実行ファイル横に状態を保存し、破損時はバックアップを検証する。
internal sealed class StateStore(string file)
{
    private static readonly JsonSerializerOptions s_s_format = new(); // Same schema; omit indentation in large inventories.
    private readonly string __backup = file + ".bak", __temporary = file + ".tmp", __recoveryPrefix = file + ".broken-";
    private readonly string __database = Path.ChangeExtension(file, ".sqlite3");
    private readonly HashSet<string> __dbFiles = new(new[] { "", "-journal", "-wal", "-shm" }.Select(s => Path.ChangeExtension(file, ".sqlite3") + s), StringComparer.OrdinalIgnoreCase);
    private HashSet<string> __published = [];
    private bool __prune;

    // 名前だけでなく保存先の絶対パスを照合し、別フォルダの同名ファイルは監視する。
    internal bool IsInternalFile(string path)
    {
        var name = Path.GetFullPath(path);
        if (__dbFiles.Contains(name) || name.Equals(file, StringComparison.OrdinalIgnoreCase) ||
            name.Equals(__backup, StringComparison.OrdinalIgnoreCase) ||
            name.Equals(__temporary, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return name.StartsWith(__recoveryPrefix, StringComparison.OrdinalIgnoreCase) &&
            Guid.TryParseExact(name.AsSpan(__recoveryPrefix.Length), "N", out _);
    }

    // 旧版キャッシュを基準にした初回巡回で、内部ファイルを「消失」と誤通知しない。
    private AppState RemoveInternalObservations(AppState state)
    {
        foreach (var item in state.Items)
        {
            if (!item.IsFolder)
            {
                if (IsInternalFile(item.Path))
                {
                    item.Unread = 0;
                }
                continue;
            }

            if (item.Inventory is { } inventory)
            {
                var excluded = inventory.Keys.Where(x => IsInternalFile(Path.Combine(item.Path, x))).ToArray();
                foreach (var relative in excluded)
                {
                    inventory.Remove(relative);
                }
                if (excluded.Length != 0)
                {
                    item.SavedInventory = null;
                    item.LatestFileWriteAt = inventory.Count == 0 ? null :
                        FileChange.GetModifiedTime(inventory.Values.Max(x => x.Modified), DateTimeOffset.Now);
                }
            }

            foreach (var relative in item.PendingFiles.Keys.Where(x => IsInternalFile(Path.Combine(item.Path, x))).ToArray())
            {
                item.Unread = Math.Max(0, item.Unread - item.PendingFiles[relative].Count);
                item.PendingFiles.Remove(relative);
            }
        }
        return state;
    }

    /// @brief 指定した実行ディレクトリ内だけで設定を初期化する。
    internal static StateStore BesideExecutable(string directory)
    {
        var destination = Path.Combine(directory, AppDefaults.CacheFileName);
        var store = new StateStore(destination);
        // exe の隣に設定本体があれば、それを読み込み先として優先する。
        if (File.Exists(destination))
        {
            return store;
        }

        // 本体がなく同じ場所にバックアップがあれば、初期化せずそこから復元する。
        if (File.Exists(destination + ".bak"))
        {
            store.Save(store.Read(destination + ".bak"));
            return store;
        }
        store.Save(new AppState());
        return store;
    }

    /// @brief 本体を優先し、読めない場合はバックアップを検証する。
    public (AppState State, string? Warning) Load()
    {
        // 本体もバックアップもない保存先では、警告なしの初期状態を返す。
        if (!File.Exists(file) && !File.Exists(file + ".bak"))
        {
            return (new(), null);
        }

        try
        {
            var state = RemoveInternalObservations(Read(file));
            __published = SnapshotIds(state);
            if (state.Version == 1 && state.Items.Any(i => i.Inventory != null))
            {
                // A migration write error isn't a corrupt source: never roll back to an older backup.
                try { Save(state); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { return (state, "比較一覧を移行できませんでした。保存時に再試行します：" + ex.Message); }
            }
            return (state, null);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return RecoverBackup(ex);
        }
    }

    /// @brief バックアップの検証が済むまで壊れた本体には手を加えない。
    private (AppState State, string Warning) RecoverBackup(Exception primaryError)
    {
        AppState backup;
        try
        {
            backup = Read(file + ".bak");
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            throw new IOException("設定とバックアップを読み込めません。元のファイルは上書きしていません。",
                new AggregateException(primaryError, error));
        }

        // 破損した本体が残っている場合だけ、検証済みバックアップへの復元前に退避する。
        if (File.Exists(file))
        {
            ArchivePrimary();
        }

        RemoveInternalObservations(backup);
        try
        {
            // 退避後の本体を直ちに作り直す。次の巡回や既読操作を待たず、バックアップは保持する。
            Save(backup);
            return (backup, "メインの設定を読み込めなかったため、バックアップから復元しました。");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return (backup, "バックアップを読み込みましたが、設定ファイルを復元できませんでした：" + error.Message);
        }
    }

    /// @brief 破損した本体を別名で保持する。
    private void ArchivePrimary() => File.Move(file, file + ".broken-" + Guid.NewGuid().ToString("N"));

    /// @brief 入力から観測値または保存状態を読み込む。
    private AppState Read(string path)
    {
        using var stream = File.OpenRead(path);
        var data = JsonSerializer.Deserialize<AppState>(stream) ?? throw new JsonException();
        // 未対応バージョンの設定は、現行形式として解釈しない。
        if (data.Version is not (1 or 2))
        {
            throw new NotSupportedException("この設定ファイルのバージョンには対応していません。");
        }

        // 監視対象の欠落・不正なパス・ID の重複があれば、壊れた設定として扱う。
        if (data.Items is null || data.Items.Any(x => x is null || string.IsNullOrWhiteSpace(x.Path)) ||
            data.Items.GroupBy(x => x.Id).Any(x => x.Count() != 1))
        {
            throw new JsonException("監視リストが壊れています。");
        }

        ValidateItems(data.Items);
        // 旧版は実効値のみ保存。既定値と異なるフォルダの監視範囲を保持する。
        data.RecursiveFolders ??= true;
        foreach (var item in data.Items.Where(i => i.IsFolder && i.Recursive != data.RecursiveFolders))
            item.RecursiveOverride ??= item.Recursive;
        // 履歴が欠落しているかパスを特定できない項目があれば読み込みを中止する。
        if (data.History is null || data.History.Any(x => x is null || string.IsNullOrWhiteSpace(x.Path)))
        {
            throw new JsonException("変更履歴が壊れています。");
        }

        while (data.History.Count > AppDefaults.HistoryLimit)
        {
            data.History.Dequeue();
        }

        using var db = data.Items.Any(i => i.InventorySnapshot != null) ? new InventoryDb(__database, false) : null;
        foreach (var item in data.Items)
        {
            if (item.InventorySnapshot is string id) item.Inventory = db!.Read(id);
            NormalizeDictionaries(item);
            if (item.InventorySnapshot != null) item.SavedInventory = item.Inventory;
            item.InventoryExclusions = FileExclusions.Normalize(item.InventoryExclusions);
        }

        data.ExcludedFilePatterns = FileExclusions.Normalize(data.ExcludedFilePatterns);
        data.PollSeconds = Math.Clamp(data.PollSeconds, AppDefaults.MinimumPollSeconds, AppDefaults.MaximumPollSeconds);
        return data;
    }

    /// @brief 登録の重複・比較規則・データ形状を検証する。
    private static void ValidateItems(List<WatchItem> items)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            try
            {
                item.Path = item.IsFolder ? FileProbe.NormalizeFolder(item.Path) : FileProbe.Normalize(item.Path);
                // 正規化後のパスが重複する設定や負の未読件数は受け付けない。
                if (!paths.Add(item.Path) || item.Unread < 0)
                {
                    throw new JsonException("無効な監視対象があります。");
                }
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
            {
                throw new JsonException("監視対象のパスが無効です。", ex);
            }
        }
    }

    /// @brief 登録の重複・比較規則・データ形状を検証する。
    private static void NormalizeDictionaries(WatchItem item)
    {
        try
        {
            item.PendingFiles = new(item.PendingFiles ?? [], StringComparer.OrdinalIgnoreCase);
            // 保存された比較一覧がある場合だけ、大文字小文字を区別しない辞書に戻す。
            if (item.Inventory is not null)
            {
                item.Inventory = new(item.Inventory, StringComparer.OrdinalIgnoreCase);
            }

            // 不正な子パス・指紋・変更記録を含む一覧は、監視やシェル操作に渡さない。
            if (item.Inventory?.Any(entry => entry.Value is null || !IsRelativeEntry(entry.Key)) == true ||
                item.PendingFiles.Any(entry => !(item.IsFolder && entry.Key == ".") && !IsRelativeEntry(entry.Key) || entry.Value is null ||
                    entry.Value.Relative != entry.Key || entry.Value.Count < 1))
            {
                throw new JsonException("変更記録が壊れています。");
            }
        }
        catch (ArgumentException ex) { throw new JsonException("大小文字だけが異なる記録が重複しています。", ex); }
    }

    /// @brief 保存済みの子パスを UI やシェルへ渡す前に、監視先内の相対名であることを確認する。
    private static bool IsRelativeEntry(string relativePath)
    {
        // 実際の列挙で生成されない絶対パス、親参照、代替データストリームは受け付けない。
        // 子の識別子に絶対パスが入っていたら、監視先からの相対名として使わない。
        if (Path.IsPathRooted(relativePath))
        {
            return false;
        }

        return relativePath.Split('\\', '/').All(part =>
            part.Length > 0 && part is not "." and not ".." && part.IndexOfAny(Path.GetInvalidFileNameChars()) < 0);
    }

    /// @brief 状態を保存し、失敗時に既存データを保護する。
    public void Save(AppState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var dirty = state.Items.Where(i => i.Inventory != null && !ReferenceEquals(i.Inventory,i.SavedInventory)).ToArray();
        if (dirty.Length > 0)
        {
            using var db = new InventoryDb(__database, true);
            db.Exec("BEGIN IMMEDIATE");
            var ids = dirty.Select(i => db.Write(i.Inventory!)).ToArray();
            db.Exec("COMMIT");
            for (int n = 0; n < ids.Length; n++) { dirty[n].InventorySnapshot = ids[n]; dirty[n].SavedInventory = dirty[n].Inventory; }
            __prune = true;
        }
        foreach (var item in state.Items.Where(i => i.Inventory == null)) { item.InventorySnapshot = null; item.SavedInventory = null; }
        state.Version = 2;
        var current = SnapshotIds(state);
        // 同じフォルダに一時ファイルを置く。Flush後に置換して部分書き込みを見せない。
        string temporary = file + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, state, s_s_format);
                stream.Flush(flushToDisk: true);
            }
            // 既存の設定はバックアップを残して置換する。
            if (File.Exists(file))
            {
                File.Replace(temporary, file, file + ".bak", true);
            }
            // 初回保存では置換先がないため、一時ファイルを本体に移す。
            else
            {
                File.Move(temporary, file);
            }
            if ((__prune || !__published.SetEquals(current)) && File.Exists(__database))
            {
                var keep = new HashSet<string>(current);
                // Read the actual backup, not the last in-memory state (important after recovery).
                if (File.Exists(__backup))
                {
                    using var backup = File.OpenRead(__backup);
                    using var json = JsonDocument.Parse(backup);
                    foreach (var i in json.RootElement.GetProperty("Items").EnumerateArray())
                        if (i.TryGetProperty("InventorySnapshot", out var id) && id.ValueKind == JsonValueKind.String) keep.Add(id.GetString()!);
                }
                using var db = new InventoryDb(__database,true);
                db.Exec("BEGIN IMMEDIATE"); db.Prune(keep); db.Exec("COMMIT");
                __prune = !keep.SetEquals(current);
            }
            __published = current;
        }
        finally
        {
            // 保存後やエラー後に一時ファイルが残っていれば片付ける。
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
    private static HashSet<string> SnapshotIds(AppState state) => state.Items.Select(i => i.InventorySnapshot).OfType<string>().ToHashSet();
}
