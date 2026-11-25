using System.Runtime.InteropServices;

namespace ShareFileWatch;

/// @brief 隔離したファイルを使って監視処理を検証する。
internal static class SelfTest
{
    /// @brief 隔離したファイルを使って監視処理を検証する。
    public static int Run(string report, bool ui)
    {
        try
        {
            using var root = Directory.CreateTempSubdirectory("ShareFileWatchQA-").AsFixture();
            var lines = new List<string>();
            // 画面テスト指定時は一時環境で WinForms の操作を検証する。
            if (ui)
            {
                TestWindow(root.FullName, lines, Path.GetFileName(report).Contains("preview", StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(report) : null);
            }
            // 通常の自己テストでは設定の永続化とファイル・フォルダ監視を検証する。
            else
            {
                TestCache(root.FullName, lines);
                TestPortableStore(root.FullName, lines);
                TestDisk(root.FullName, lines);
                TestFolders(root.FullName, lines, "local");
                TestSmb(root.FullName, lines);
            }
            File.WriteAllLines(report, lines);
            return 0;
        }
        catch (Exception error) { File.WriteAllText(report, error.ToString()); return 1; }
    }

    /// @brief 期待値と異なる結果をテスト失敗として報告する。
    internal static void Check(bool ok, string description)
    {
        // 期待した条件を満たさない場合はテスト失敗として理由を報告する。
        if (!ok)
        {
            throw new InvalidOperationException("FAIL: " + description);
        }
    }

    /// @brief 一時環境で永続化またはファイル監視の契約を検証する。
    private static void TestCache(string root, List<string> lines)
    {
        var mark = DateTimeOffset.Now;
        Check(MainWindow.RelativeAge(mark.AddMinutes(-3), mark) == "3分前", "age formatting");
        var file = Path.Combine(root, "cache.json");
        var store = new StateStore(file);
        var item = new WatchItem { Path = Path.Combine(root, "test.txt") };
        var state = new AppState { Items = [item], PollSeconds = 15 };
        var at = DateTimeOffset.Now;
        Check(item.Observe(new(ProbeState.Found, new(1, 2, 3)), at) is null, "first lookup is silent");
        Check(item.Observe(new(ProbeState.Unavailable), at) is null && item.Unread == 0, "disconnect not deletion");
        store.Save(state);

        var reopened = new StateStore(file).Load().State;
        item = reopened.Items.Single();
        Check(reopened.PollSeconds == 15, "poll period survives reload");
        Check(item.Observe(new(ProbeState.Found, new(2, 2, 3)), at) == "更新" && item.Unread == 1, "detect update since shutdown");
        store.Save(reopened);

        reopened = new StateStore(file).Load().State;
        item = reopened.Items.Single();
        Check(item.Unread == 1, "unread survives reload");
        Check(item.Observe(new(ProbeState.Found, new(2, 2, 3)), at) is null && item.Unread == 1, "no duplicate alert");
        item.Acknowledge();
        store.Save(reopened);
        Check(new StateStore(file).Load().State.Items.Single() is { Unread: 0, AcknowledgedAt: not null }, "ack survives reload");

        File.WriteAllText(file, "{");
        var restored = store.Load();
        Check(restored.Warning is not null && restored.State.Items.Single().Unread == 1, "recover previous valid backup");
        Check(Directory.GetFiles(root, "cache.json.broken-*").Length == 1, "preserve corrupt source");
        var invalidStore = new StateStore(Path.Combine(root, "invalid-child.json"));
        item.PendingFiles.Add(@"..\outside", new FileChange(@"..\outside", "更新", at));
        invalidStore.Save(reopened);
        bool rejected = false;
        try
        {
            invalidStore.Load();
        }
        catch (IOException) { rejected = true; }
        Check(rejected, "reject a cached child path outside its registered folder");
        lines.Add("PASS: cache, reload, offline comparison, unread, acknowledge, recovery");
    }

    /// @brief 一時環境で永続化またはファイル監視の契約を検証する。
    private static void TestPortableStore(string root, List<string> lines)
    {
        var exeDir = Path.Combine(root, "portable app");
        var dest = Path.Combine(exeDir, "cache.json");
        var old = Path.Combine(root, "legacy", "cache.json");
        var state = new AppState { PollSeconds = 17, DarkMode = true };
        state.History.Enqueue(new(DateTimeOffset.Now, Path.Combine(root, "sample.txt"), "更新"));
        var legacy = new StateStore(old);
        var fresh = Path.Combine(root, "first launch");
        legacy.Save(state);
        var blank = StateStore.BesideExecutable(fresh).Load().State;
        Check(blank.Items.Count == 0 && blank.History.Count == 0 && !blank.DarkMode && File.Exists(Path.Combine(fresh, "cache.json")), "new directory has independent settings and history");
        var store = StateStore.BesideExecutable(exeDir);
        store.Save(state);
        state.PollSeconds = 33;
        store.Save(state);
        Check(StateStore.BesideExecutable(exeDir).Load().State.PollSeconds == 33 && File.Exists(dest + ".bak") && !File.Exists(dest + ".tmp"), "portable state persists beside exe");
        File.Delete(dest);
        Check(StateStore.BesideExecutable(exeDir).Load().State.PollSeconds == 17 && File.Exists(dest) && File.Exists(dest + ".bak"), "missing portable primary recreated from local backup");
        lines.Add("PASS: isolated portable settings and history, same-directory backup recovery");
    }

    /// @brief 一時環境で永続化またはファイル監視の契約を検証する。
    private static void TestDisk(string root, List<string> lines)
    {
        var folder = Directory.CreateDirectory(Path.Combine(root, "local")).FullName;
        var a = Path.Combine(folder, "sample.txt");
        var b = Path.Combine(folder, "keep.txt");
        File.WriteAllText(a, "first");
        File.WriteAllText(b, "keep");
        var p1 = FileProbe.Read(FileProbe.Normalize(a));
        Check(p1.State == ProbeState.Found, "found actual file");

        using (var writer = new FileStream(a, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            Check(FileProbe.Read(a).State == ProbeState.Found, "metadata accessible with exclusive data handle");
        }

        var first = new WatchItem { Path = a };
        var second = new WatchItem { Path = b };
        first.Observe(p1, DateTimeOffset.Now);
        second.Observe(FileProbe.Read(b), DateTimeOffset.Now);
        File.AppendAllText(a, " second");
        Check(first.Observe(FileProbe.Read(a), DateTimeOffset.Now) == "更新", "file changed");
        Check(second.Observe(FileProbe.Read(b), DateTimeOffset.Now) is null, "other file unchanged");
        File.Delete(a);
        Check(first.Observe(FileProbe.Read(a), DateTimeOffset.Now) == "消失", "file deleted");
        File.WriteAllText(a, "third");
        Check(first.Observe(FileProbe.Read(a), DateTimeOffset.Now) == "出現", "file reappeared");
        Directory.Delete(folder, recursive: true);
        Check(FileProbe.Read(a).State == ProbeState.Unavailable, "parent folder removed");
        lines.Add("PASS: Windows real files, exclusive writer, independent files, delete/reappear, parent removal");
    }

    /// @brief 一時環境で永続化またはファイル監視の契約を検証する。
    private static void TestSmb(string root, List<string> lines)
    {
        var unc = @"\\localhost\" + root[0] + "$" + root[2..];
        // ループバック共有にアクセスできない環境では SMB 試験だけをスキップする。
        if (!Directory.Exists(unc))
        {
            lines.Add("SKIP: SMB loopback shares unavailable");
            return;
        }
        TestFolders(unc, lines, "SMB loopback");
        var folder = Directory.CreateDirectory(Path.Combine(root, "loopback")).FullName;
        var localFile = Path.Combine(folder, "sample.txt");
        var sharedFile = FileProbe.Normalize(Path.Combine(unc, "loopback", "sample.txt"));
        File.WriteAllText(localFile, "fixture");
        var item = new WatchItem { Path = sharedFile };
        var found = FileProbe.Read(sharedFile);
        Check(found.State == ProbeState.Found, $"SMB found, actual={found.Error}");
        item.Observe(found, DateTimeOffset.Now);
        var ready = new ManualResetEventSlim(false);
        var stop = new CancellationTokenSource();
        Task worker = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                FileProbe.Read(sharedFile);
                ready.Set();
            }
        });
        try
        {
            Check(ready.Wait(TimeSpan.FromSeconds(4)), "SMB polling worker started");
            Check(DeleteFileW(sharedFile), "SMB concurrent file delete; error " + Marshal.GetLastWin32Error());
            Check(RemoveDirectoryW(Path.GetDirectoryName(sharedFile)!), "SMB concurrent folder delete; error " + Marshal.GetLastWin32Error());
        }
        finally { stop.Cancel(); worker.GetAwaiter().GetResult(); ready.Dispose(); stop.Dispose(); }
        Check(FileProbe.Read(sharedFile).State == ProbeState.Unavailable, "SMB deleted parent yields unavailable");
        lines.Add("PASS: SMB loopback real lookup and file/folder deletion during continuous polling");
    }

    /// @brief 一時環境で永続化またはファイル監視の契約を検証する。
    private static void TestFolders(string root, List<string> lines, string medium)
    {
        var folder = Directory.CreateDirectory(Path.Combine(root, medium == "local" ? "inventory" : "inventory-smb")).FullName;
        var child = Directory.CreateDirectory(Path.Combine(folder, "child")).FullName;
        var direct = Path.Combine(folder, "direct.txt");
        var nested = Path.Combine(child, "nested.txt");
        var now = DateTimeOffset.Now;
        var empty = FolderProbe.Read(child, false);
        Check(empty.Files is { Count: 0 }, medium + " empty folder: " + empty.Error + " count=" + empty.Files?.Count);
        File.WriteAllText(direct, "one");
        File.WriteAllText(nested, "one");
        var flat = new WatchItem { Path = folder, IsFolder = true };
        var deep = new WatchItem { Path = folder, IsFolder = true, Recursive = true };
        var f = FolderProbe.Read(folder, false);
        var d = FolderProbe.Read(folder, true);
        Check(f.Files is { Count: 1 } && d.Files is { Count: 2 }, medium + " distinct scan scopes");
        Check(flat.ObserveFolder(f, now) is null && deep.ObserveFolder(d, now) is null, "initial inventory silent");
        File.AppendAllText(nested, " changed size");
        Check(flat.ObserveFolder(FolderProbe.Read(folder, false), now) is null, "nested change ignored by flat scan");
        Check(deep.ObserveFolder(FolderProbe.Read(folder, true), now)?.Contains(@"child\nested.txt") == true &&
            deep.Unread == 1, "nested edit detected");
        deep.ObserveFolder(new(null, "disconnected"), now);
        Check(deep.Unread == 1 && deep.Inventory?.Count == 2, "interruption keeps inventory");
        var store = new StateStore(Path.Combine(root, medium == "local" ? "folder-cache.json" : "folder-smb-cache.json"));
        store.Save(new()
        {
            Items = [deep]
        });
        deep = store.Load().State.Items.Single();
        Check(deep.IsFolder && deep.Recursive && deep.Unread == 1 && deep.PendingFiles.Count == 1,
              "folder settings and one unread detail survive restart");
        Check(deep.ObserveFolder(FolderProbe.Read(folder, true), now) is null, "restart does not duplicate");
        File.Delete(direct);
        File.WriteAllText(Path.Combine(child, "new.txt"), "new");
        Check(deep.ObserveFolder(FolderProbe.Read(folder, true), now)?.StartsWith("2件") == true &&
            deep.LastChange.Contains("+1 / 更新0 / 消失1"), "create and delete compared after restart");
        var prior = deep.PendingFiles[@"child\nested.txt"];
        deep.AcknowledgeFile(prior);
        Check(deep.Unread == 2 && deep.PendingFiles.Count == 2, "reading one file preserves siblings");
        deep.SetRecursive(false);
        Check(deep.ObserveFolder(FolderProbe.Read(folder, false), now) is null && deep.Unread == 2, "scope switch rebaselines");
        lines.Add($"PASS: {medium} folder inventory, flat/recursive, changes, outage, restart and scope switch");
    }

    /// @brief 一時環境で永続化またはファイル監視の契約を検証する。
    private static void TestWindow(string root, List<string> lines, string? previewDirectory)
    {
        var sample = Path.Combine(root, "window.txt");
        File.WriteAllText(sample, "test");
        var cache = Path.Combine(root, "ui-cache.json");
        var store = new StateStore(cache);
        bool allowOpen = false;
        using var window = new MainWindow(store, testMode: true, launchFolder: requested =>
        {
            Check(Directory.Exists(requested) && (requested == root || requested == Path.Combine(root, "gui-folder") || requested == Path.Combine(root, "gui-folder", "sub")), "folder action targets a test folder");
            return allowOpen;
        });
        window.StartPosition = FormStartPosition.Manual;
        window.Location = new Point(-25000, -25000);
        window.ShowInTaskbar = false;
        Exception? error = null;
        window.Shown += async (_, _) =>
        {
            try
            {
                await window.ExerciseUi(sample, cache, () => allowOpen = true, previewDirectory);
            }
            catch (Exception ex) { error = ex; Application.ExitThread(); }
        };
        Application.Run(window);
        // UI スレッドで捕捉した例外があれば、テスト全体に失敗を伝える。
        if (error is not null)
        {
            throw error;
        }

        Check(store.Load().State.Items.First().AcknowledgedAt is not null, "GUI acknowledgment persisted");
        Check(!store.Load().State.KeepInTray, "tray setting persisted");
        lines.Add("PASS: WinForms, native Shell menu path/commands, click vs double-click and acknowledgement, idle cache preservation, folder target, failed/successful open acknowledgement, mixed drop, per-folder recursion, tray hide/restore and disabled close, persisted options");
    }

    /// @brief 削除結果を Win32 エラーとともに検証するためのテスト用 API。
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteFileW(string path);

    /// @brief 削除結果を Win32 エラーとともに検証するためのテスト用 API。
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveDirectoryW(string path);

    /// @brief 試験終了時に片付ける一時ディレクトリ。
    private sealed class Fixture(DirectoryInfo directory) : IDisposable
    {
        /// @brief 試験終了時に片付ける一時ディレクトリ。
        public string FullName => directory.FullName;
        /// @brief 所有するリソースを解放する。
        public void Dispose()
        {
            var parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
            // 削除対象の名前と場所が試験用の条件を満たす場合だけ、一時ディレクトリを片付ける。
            if (directory.Name.StartsWith("ShareFileWatchQA-", StringComparison.Ordinal) &&
                string.Equals(directory.Parent?.FullName, parent, StringComparison.OrdinalIgnoreCase))
            {
                directory.Delete(recursive: true);
            }
        }
    }

    /// @brief 試験終了時に片付ける一時ディレクトリ。
    private static Fixture AsFixture(this DirectoryInfo folder) => new(folder);
}
