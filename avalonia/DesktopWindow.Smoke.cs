#if KAWATTAYO_SMOKE
using Avalonia.Controls;
using Avalonia.Threading;

namespace ShareFileWatch;

internal sealed partial class DesktopWindow
{
    // テスト用ビルドだけがこの入口を持つ。実行ファイル横のデータを一切使わない。
    internal void AttachSmoke(string report, string fixture)
    {
        Opened += async (_, _) =>
        {
            try
            {
                Width = MinWidth;
                Height = MinHeight;
                var folder = Directory.CreateDirectory(Path.Combine(fixture, "監視テスト")).FullName;
                var nested = Directory.CreateDirectory(Path.Combine(folder, "配下")).FullName;
                var sample = Path.Combine(nested, "記録.txt");
                File.WriteAllText(sample, "初期");
                await Register([folder]);
                await __monitor.Scan();
                var item = __state.Items.Single();
                Require(item.Inventory?.Count == 1 && item.Unread == 0, "初回照会・再帰監視と対象数");
                File.AppendAllText(sample, "追記");
                await __monitor.Scan();
                Refresh(true);
                Require(__inbox.Items.Count == 1 && item.Unread == 1 && __history.Items.Count == 1, "未読表示と履歴");
                ExpandAll();
                Require(__inbox.Items.Count == 3 && __inbox.Items.Cast<Row>().Last().Path == sample, "再帰ツリーの展開");
                __targets.SelectedIndex = 0;
                Require(Selected(__targets).Single().Count == 1, "監視対象列");
                __tabs.SelectedIndex = 2;
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                Require(Content is DockPanel && IsVisible, "実ウィンドウと最小サイズの設定");
                await __monitor.Close();
                var store = StateStore.BesideExecutable(fixture);
                var saved = store.Load().State;
                Require(saved.Items.Single().PendingFiles.Count == 1 && saved.History.Single().Path == sample, "再起動時のSQLite復元");
                var json = File.ReadAllText(Path.Combine(fixture, AppDefaults.SettingsFileName));
                Require(!json.Contains("PendingFiles") && !json.Contains("History") && !json.Contains("Inventory"), "設定専用JSON");
                File.WriteAllText(report, "PASS: Avalonia window, registration, scan, unread tree, history and SQLite restart.");
            }
            catch (Exception error) { File.WriteAllText(report, error.ToString()); }
            finally { __closed = true; Close(); }
        };
    }

    private static void Require(bool result, string scenario)
    {
        if (!result) throw new InvalidOperationException("失敗：" + scenario);
    }
}
#endif
