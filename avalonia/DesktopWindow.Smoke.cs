#if KAWATTAYO_SMOKE
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

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
                await VerifySaveRetry();
                Width = MinWidth;
                Height = MinHeight;
                var folder = Directory.CreateDirectory(Path.Combine(fixture, "監視テスト")).FullName;
                var nested = Directory.CreateDirectory(Path.Combine(folder, "配下")).FullName;
                var sample = Path.Combine(nested, "記録.txt");
                VerifyPendingLookup(folder);
                File.WriteAllText(sample, "初期");
                await Register([folder]);
                await __monitor.Scan();
                var item = __state.Items.Single();
                Require(item.Inventory?.Count == 1 && item.Unread == 0, "初回照会・再帰監視と対象数");
                File.AppendAllText(sample, "追記");
                await __monitor.Scan();
                Refresh(true);
                __tabs.SelectedIndex = 3;
                __tabs.SelectedIndex = 0;
                RefreshHistory();
                Require(__inbox.Items.Count == 1 && item.Unread == 1 && __history.Items.Count == 1, "未読表示と履歴");
                ExpandAll();
                Require(__inbox.Items.Count == 3 && __inbox.Items.Cast<Row>().Last().Path == sample, "再帰ツリーの展開");
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                var collapse = this.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "すべて折りたたむ"));
                Require(Title == "かわったよ v0.2.2 · Avalonia", "タイトルのバージョン");
                Require(collapse is { IsEffectivelyVisible: true, Bounds.Width: > 0, Bounds.Height: > 0 }
                    && collapse.TranslatePoint(default, this) is { } location
                    && location.X >= 0 && location.Y >= 0
                    && location.X + collapse.Bounds.Width <= ClientSize.Width
                    && location.Y + collapse.Bounds.Height <= ClientSize.Height, "最小幅の未読タブにボタンを表示");
                collapse.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
                Require(__expanded.Count == 0 && __inbox.Items.Count == 1, "ボタン操作で一括折りたたみ");
                __tabs.SelectedIndex = 1;
                RefreshTargets();
                __targets.SelectedIndex = 0;
                Require(Selected(__targets).Single().Count == 1, "監視対象列");
                __tabs.SelectedIndex = 2;
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                Require(Content is DockPanel && IsVisible, "実ウィンドウと最小サイズの設定");
                // 読了直後、結果の反映前に監視条件が変わるケースを競合なしで再現する。
                File.AppendAllText(sample, "旧条件の追加");
                bool invalidated = false;
                __monitor.BeforeApplyForTest = () =>
                {
                    __monitor.BeforeApplyForTest = null;
                    invalidated = true;
                    __state.ExcludedFilePatterns = ["記録.txt"];
                    __monitor.UpdateSettings(rescan: true);
                };
                await __monitor.Scan();
                Require(invalidated && item.PendingFiles["配下\\記録.txt"].Count == 1 && __state.History.Count == 1,
                    "除外ルール変更前の照会結果を破棄");
                await __monitor.Close();
                var store = StateStore.BesideExecutable(fixture);
                var saved = store.Load().State;
                Require(saved.Items.Single().PendingFiles.Count == 1 && saved.History.Single().Path == sample, "再起動時のSQLite復元");
                var json = File.ReadAllText(Path.Combine(fixture, AppDefaults.SettingsFileName));
                Require(!json.Contains("PendingFiles") && !json.Contains("History") && !json.Contains("Inventory"), "設定専用JSON");
                File.WriteAllText(report, "PASS: Avalonia window, scan, stale result suppression, save retry, large unread lookup and SQLite restart.");
            }
            catch (Exception error) { File.WriteAllText(report, error.ToString()); }
            finally { __closed = true; Close(); }
        };
    }

    private static async Task VerifySaveRetry()
    {
        int attempts = 0;
        int saved = 0;
        var state = new AppState { PollSeconds = 15 };
        var writer = new SnapshotWriter(state.CaptureForSave, snapshot =>
        {
            if (Interlocked.Increment(ref attempts) <= 2) throw new IOException("模擬：書き込み不可");
            saved = snapshot.PollSeconds;
        }, () => { });
        writer.Enqueue();
        try
        {
            await writer.FlushAsync();
            throw new InvalidOperationException("保存エラーを無視した");
        }
        catch (IOException) { Require(writer.Error is not null && saved == 0, "失敗時に保存済みにしない"); }
        state.PollSeconds = 35;
        writer.Enqueue();
        await writer.FlushAsync();
        Require(saved == 35 && writer.Error is null, "保存先が復帰した後に最新状態を保存");
    }

    private static void VerifyPendingLookup(string folder)
    {
        var target = new WatchItem { Path = folder, IsFolder = true };
        var at = DateTimeOffset.Now;
        target.PendingFiles["."] = new(".", "更新", at);
        for (int i = 0; i < 30_000; ++i)
        {
            var path = $"配下\\{i}.txt";
            target.PendingFiles[path] = new(path, "更新", at);
        }

        // 本番の照会と同じく、未読が大きくても確認するのは対象パスと祖先だけ。
        var check = new PendingPathLookup([target]);
        Require(check.Contains(folder), "フォルダ自身の未読");
        Require(check.Contains(Path.Combine(folder, "配下", "29999.txt")), "大量未読の子パス");
        Require(!check.Contains(Path.Combine(folder, "配下", "なし.txt")), "未登録パスは未読扱いしない");
    }

    private static void Require(bool result, string scenario)
    {
        if (!result) throw new InvalidOperationException("失敗：" + scenario);
    }
}
#endif
