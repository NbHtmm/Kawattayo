#if KAWATTAYO_SMOKE
using System.Diagnostics;
using System.Text.Json;

namespace ShareFileWatch;

internal static class InventoryTests
{
    internal static void TestSplit(string root)
    {
        string dir = Directory.CreateDirectory(Path.Combine(root, "split-settings")).FullName;
        string json = Path.Combine(dir, "settings.json"), db = Path.Combine(dir, "cache.sqlite3");
        // 旧版は読み込まず、内容にも手を加えない。
        string old = Path.Combine(dir, "cache.json");
        File.WriteAllText(old, """{"Version":2,"PollSeconds":87,"History":[{"Path":"legacy-private.txt"}]}""");
        var store = StateStore.BesideExecutable(dir);
        var state = store.Load().State;
        Check(state.PollSeconds == AppDefaults.DefaultPollSeconds && state.History.Count == 0 &&
            File.ReadAllText(old).Contains("legacy-private.txt"), "old JSON untouched and ignored");

        var at = DateTimeOffset.Now;
        var item = new WatchItem { Path = dir, IsFolder = true, Recursive = true };
        const string child = "観測だけ.txt";
        item.ObserveFolder(new(new(StringComparer.OrdinalIgnoreCase) { [child] = new(10, 20, 30) }), at);
        item.ObserveFolder(new(new(StringComparer.OrdinalIgnoreCase) { [child] = new(11, 20, 31) }), at);
        state.Items.Add(item);
        state.History.Enqueue(new(at, Path.Combine(dir, child), "更新"));
        state.PollSeconds = 37;
        store.Save(state);

        var settingsBefore = File.ReadAllBytes(json);
        string backup = json + ".bak";
        Check(!File.Exists(backup) && !File.Exists(json + ".tmp"), "a successful settings write leaves no backup or temporary file");
        using (var settings = JsonDocument.Parse(settingsBefore))
        {
            string[] allowedSettings = ["Pinned", "LowServerLoad", "WatchDirectoryTime", "NotifyDeleted", "ExcludedFilePatterns",
                "TextSizePoints", "RenotifyUnread", "ReadOnFileOpen", "RecursiveFolders", "DarkMode", "Version", "PollSeconds",
                "Paused", "KeepInTray", "Items", "WaitForStable", "StableSeconds"];
            string[] allowedRegistration = ["Id", "Path", "DisplayName", "IsFolder", "RecursiveOverride", "Recursive", "WaitForStableOverride"];
            Check(settings.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet().SetEquals(allowedSettings) &&
                settings.RootElement.GetProperty("Items")[0].EnumerateObject().Select(p => p.Name).ToHashSet().SetEquals(allowedRegistration),
                "JSON contains only preferences and explicitly registered targets");
        }

        var saved = StateStore.BesideExecutable(dir).Load().State;
        var restored = saved.Items.Single();
        Check(saved.PollSeconds == 37 && saved.History.Single().Path == Path.Combine(dir, child) &&
            restored.Inventory![child].Bytes == 31 && restored.PendingFiles[child].Count == 1 && restored.Unread == 1,
            "SQLite round-trips inventory, unread and history");

        CheckRestoreAfterRegistrationChange(db, item, child);

        item.Acknowledge();
        store.Save(state);
        Check(File.ReadAllBytes(json).SequenceEqual(settingsBefore) && !File.Exists(backup),
            "activity-only saves do not touch settings or create a backup");
        saved = StateStore.BesideExecutable(dir).Load().State;
        Check(saved.Items.Single() is { Unread: 0, AcknowledgedAt: not null } && saved.History.Count == 1,
            "acknowledging remains persisted separately from history");

        var dbBefore = File.ReadAllBytes(db);
        state.PollSeconds = 91;
        store.Save(state);
        Check(File.ReadAllBytes(db).SequenceEqual(dbBefore), "settings-only save does not touch SQLite");
        Check(!File.Exists(backup) && !File.Exists(json + ".tmp"), "subsequent settings changes still leave no backup");
        File.WriteAllText(json, "{");
        try
        {
            StateStore.BesideExecutable(dir).Load();
            throw new InvalidOperationException("invalid settings without a backup must not be silently reset");
        }
        catch (IOException)
        {
            Check(File.ReadAllText(json) == "{" && !File.Exists(backup), "leave invalid settings untouched if no backup exists");
        }

        // 既存の .bak は復元に使えるが、復元後も元の .bak の内容は変えない。
        File.WriteAllBytes(backup, settingsBefore);
        var recovered = StateStore.BesideExecutable(dir).Load();
        Check(recovered.Warning is not null && recovered.State.PollSeconds == 37 &&
            recovered.State.Items.Single().Unread == 0 && recovered.State.History.Count == 1,
            "settings backup recovery does not roll SQLite back");
        Check(File.ReadAllBytes(backup).SequenceEqual(settingsBefore), "recovery leaves an existing backup untouched");

        File.Move(db, db + ".held");
        var noCache = StateStore.BesideExecutable(dir).Load().State;
        Check(noCache.PollSeconds == 37 && noCache.Items.Single().Inventory is null && noCache.History.Count == 0,
            "missing SQLite starts fresh without losing settings");
    }


    private static void CheckRestoreAfterRegistrationChange(string database, WatchItem original, string child)
    {
        var shallow = new WatchItem
        {
            Id = original.Id, Path = original.Path, IsFolder = true, Recursive = false
        };
        var moved = new WatchItem
        {
            Id = original.Id, Path = Path.Combine(original.Path, "different-root"), IsFolder = true, Recursive = true
        };

        new MonitoringCache(database).Restore(new AppState { Items = [shallow] });
        Check(shallow.Unread == 1 && shallow.PendingFiles.ContainsKey(child) &&
            shallow.Baseline is null && shallow.Inventory is null,
            "Changing recursion keeps unread detail but discards the previous comparison baseline");

        new MonitoringCache(database).Restore(new AppState { Items = [moved] });
        Check(moved.Unread == 0 && moved.PendingFiles.Count == 0 && moved.Inventory is null,
            "Reusing an ID at a different path does not bring old observations into that registration");
    }

    internal static void VerifyBulkFileRemoval(string root)
    {
        string dir = Directory.CreateDirectory(Path.Combine(root, "bulk-removal")).FullName;
        const int entries = 8_000, removed = 2_000;
        var baseline = new Dictionary<string, Fingerprint>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < entries; i++) baseline.Add($"file-{i}.txt", new(1, 2, 3));
        string[] notices = Enumerable.Range(0, removed).Select(i => Path.Combine(dir, $"file-{i}.txt")).ToArray();
        var result = FolderProbe.ReadChanges(dir, true, baseline, notices, _ => false, default);
        Check(result.Files?.Count == entries - removed && baseline.Count == entries, "large removal keeps baseline untouched");
        var offline = FolderProbe.ReadChanges(Path.Combine(dir, "offline"), true, baseline,
            [Path.Combine(dir, "offline", "file-0.txt")], _ => false, default);
        Check(offline.Files is null, "missing root is not published as a deletion");
    }


    private static void Check(bool success, string scenario)
    {
        if (!success) throw new InvalidOperationException(scenario);
    }
}
#endif
