using System.Diagnostics;

namespace ShareFileWatch;

internal sealed partial class MainWindow
{
    // Opt-in only: real local files and an isolated cache, never the user's clipboard/settings.
    internal static int MeasurePolling()
    {
        using var fixture = Directory.CreateTempSubdirectory("KawattayoPerf-").AsFixture();
        var root = Directory.CreateDirectory(Path.Combine(fixture.FullName, "files")).FullName;
        var paths = Enumerable.Range(0, 3000).Select(i => Path.Combine(root, $"file{i:D5}.txt")).ToArray();
        foreach (var path in paths) File.WriteAllText(path, "x");
        var cache = Path.Combine(fixture.FullName, "cache.json");
        using var window = new MainWindow(new StateStore(cache), testMode: true);
        window.ShowInTaskbar = false;
        window.StartPosition = FormStartPosition.Manual;
        window.Location = new Point(-25000, -25000);
        int rc = 0;
        window.Shown += async (_, _) => {
            try
            {
                var timer = new Stopwatch();
                var folder = new WatchItem { Path = root, IsFolder = true };
                window.__state.Items.Add(folder);
                async Task ScanTime(string name) {
                    timer.Restart(); await window.Scan(); timer.Stop();
                    Console.WriteLine($"{name}: {timer.Elapsed.TotalMilliseconds:F1} ms, cache {new FileInfo(cache).Length} B");
                }
                await ScanTime("folder baseline 3000");
                var target = new ScanTarget(folder.Id, root, true, false, folder.Revision, window.__state.ExcludedFilePatterns, false);
                timer.Restart(); var snapshot = window.ProbeTarget(target).Folder!; timer.Stop();
                Console.WriteLine($"enumerate: {timer.Elapsed.TotalMilliseconds:F1} ms");
                timer.Restart(); var diff = FolderProbe.Compare(folder.Inventory!, snapshot.Files!, DateTimeOffset.Now); timer.Stop();
                SelfTest.Check(diff.Count == 0 && snapshot.Files!.Count == paths.Length, "benchmark inventory");
                Console.WriteLine($"compare: {timer.Elapsed.TotalMilliseconds:F1} ms");
                timer.Restart(); window.Save(); timer.Stop(); Console.WriteLine($"save: {timer.Elapsed.TotalMilliseconds:F1} ms");
                window.__state.Items.Clear();
                window.__state.Items.AddRange(paths.Take(256).Select(p => new WatchItem { Path = p }));
                await ScanTime("individual baseline 256");
                await ScanTime("individual unchanged 256");
                foreach (var path in paths.Take(256)) File.AppendAllText(path, "y");
                await ScanTime("individual changed 256");
                SelfTest.Check(window.__store.Load().State.Items.Count(i => i.Unread == 1) == 256, "all notifications durable");
                timer.Restart(); window.RefreshRows(); timer.Stop(); Console.WriteLine($"refresh 256 unread: {timer.Elapsed.TotalMilliseconds:F1} ms");
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); rc = 1; }
            finally { window.ExitApp(); }
        };
        Application.Run(window);
        return rc;
    }
}