using System.Diagnostics;

namespace ShareFileWatch;

internal sealed partial class MainWindow
{
    internal static void TestLargeRepositoryLayoutAndPersistence(string root, List<string> lines)
    {
        var repo = Directory.CreateDirectory(Path.Combine(root, "large-local-repository")).FullName;
        var cache = Path.Combine(root, "large-watch-cache.json");
        // 現場のリポジトリには触れず、作業ツリーと .git の多数のエントリを模した試験データを作る。
        const int monitoredFiles = 12_000;
        for (int group = 0; group < 60; group++)
        {
            var directory = Directory.CreateDirectory(Path.Combine(repo,
                group < 20 ? @".git\objects" : "worktree", group.ToString("D2"))).FullName;
            for (int i = 0; i < 200; i++) File.WriteAllText(Path.Combine(directory, $"item-{i:D3}.txt"), "x");
        }
        File.WriteAllText(Path.Combine(repo, "~$excluded.docx"), "x");

        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var window = new MainWindow(new StateStore(cache), testMode: true);
        window.StartPosition = FormStartPosition.Manual;
        window.Location = new Point(-25000, -25000);
        window.ShowInTaskbar = false;
        Exception? failure = null;
        var watch = Stopwatch.StartNew();
        window.Shown += async (_, _) =>
        {
            try
            {
                var folder = new WatchItem { Path = repo, IsFolder = true, Recursive = true };
                window.__state.Items.Add(folder);
                window.__state.PollSeconds = 3;
                window.__testBeforeBackgroundSave = () =>
                {
                    entered.Set();
                    if (!release.Wait(TimeSpan.FromSeconds(20))) throw new IOException("test gate timed out");
                };

                await window.Scan();
                long scanMs = watch.ElapsedMilliseconds;
                SelfTest.Check(entered.Wait(TimeSpan.FromSeconds(3)), "large inventory entered background save");
                SelfTest.Check(window.__saveWorker is { IsCompleted: false }, "scanning and UI do not wait for cache IO");
                SelfTest.Check(window.__targetList.Columns[1].Text == "対象ファイル数" &&
                    window.__targetList.Items[0].SubItems[1].Text == monitoredFiles.ToString("N0"),
                    "settings reports exact recursive count excluding temporary files");
                await window.Scan(automatic: true);
                window.ShowManagement();
                const string renamed = "幅が狭くても名前を確認できる長いフォルダ名";
                folder.DisplayName = renamed;
                window.Save(); // 保存中に行った変更も、その後の最新版に入る。
                folder.PendingFiles["worktree\\name.txt"] = new("worktree\\name.txt", "更新", DateTimeOffset.Now);
                folder.Unread = 1;
                window.RefreshRows();
                window.Size = window.MinimumSize;
                window.__management.Size = window.__management.MinimumSize;
                await Task.Delay(100); // WinForms のメッセージポンプを実際に通る。
                var label = window.__inbox.LayoutRow(window.__inbox.Nodes[0], window.__inbox.Nodes[0].Bounds).Label;
                var seen = Rectangle.Intersect(label, window.__inbox.ClientRectangle);
                SelfTest.Check(!window.__viewport.HorizontalScroll.Visible && seen.Width >= 120 &&
                    window.__targetList.Items[0].Text == renamed && window.__targetList.Columns[0].Width >= 125,
                    $"names at minimum: scroll={window.__viewport.HorizontalScroll.Visible}, label={label}, visible={seen}, tree={window.__inbox.ClientRectangle}, name={window.__targetList.Items[0].Text}, nameWidth={window.__targetList.Columns[0].Width}");

                release.Set();
                await window.__saveWorker!;
                var reopened = new StateStore(cache).Load().State.Items.Single();
                SelfTest.Check(reopened.Inventory?.Count == monitoredFiles && reopened.DisplayName == renamed &&
                    window.__saveError is null, "background save persists latest settings and complete inventory");
                lines.Add($"PASS: recursive .git-style tree, {monitoredFiles:N0} files, {scanMs:N0} ms until UI result; settings and minimum-size inbox respond while disk save is held; restart preserves exact count");
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                release.Set();
                window.ExitApp();
            }
        };
        Application.Run(window);
        if (failure is not null) throw new InvalidOperationException("Large scan UI regression", failure);
    }
}
