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
                var inventoryFixture = Directory.CreateDirectory(Path.Combine(fixture, "inventory")).FullName;
                InventoryTests.TestSplit(inventoryFixture);
                InventoryTests.VerifyBulkFileRemoval(inventoryFixture);
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
                var historyButton = this.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "履歴"));
                historyButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
                Require(__historyWindow is { IsVisible: true, Title: "かわったよ | 変更履歴" }, "独立した履歴ウィンドウ");
                Require(__inbox.Items.Count == 1 && item.Unread == 1 && __history.Items.Count == 1, "未読表示と履歴");
                ExpandAll();
                Require(__inbox.Items.Count == 3 && __inbox.Items.Cast<Row>().Last().Path == sample, "再帰ツリーの展開");
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                var collapse = this.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "すべて折りたたむ"));
                Require(Title == "かわったよ v0.2.6 · Avalonia", "タイトルのバージョン");
                Require(collapse is { IsEffectivelyVisible: true, Bounds.Width: > 0, Bounds.Height: > 0 }
                    && collapse.TranslatePoint(default, this) is { } location
                    && location.X >= 0 && location.Y >= 0
                    && location.X + collapse.Bounds.Width <= ClientSize.Width
                    && location.Y + collapse.Bounds.Height <= ClientSize.Height, "最小幅の未読タブにボタンを表示");
                collapse.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
                Require(__expanded.Count == 0 && __inbox.Items.Count == 1, "ボタン操作で一括折りたたみ");
                var details = this.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "詳細 ▾"));
                Require(details is { IsEffectivelyVisible: true, Bounds.Width: > 0, Bounds.Height: > 0 },
                    "最小幅でも詳細ボタンが表示される");
                Require(!__detailsBox.IsVisible, "詳細は初期状態で閉じている");
                ExpandAll();
                __inbox.SelectedIndex = __inbox.Items.Count - 1;
                details.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
                Require(__detailsBox.IsVisible && __detailsBox.Text?.Contains(sample) == true, "詳細に選択項目のパスを表示");
                details.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
                Require(!__detailsBox.IsVisible, "ボタンを再度押すと詳細を閉じる");
                var settingsButton = this.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "設定…"));
                settingsButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
                Require(__settingsWindow is { IsVisible: true, Title: "かわったよ | 設定" } &&
                    __settingsTabs.Items.Cast<TabItem>().Select(tab => tab.Header).SequenceEqual(new object[] { "監視対象", "動作と表示" }),
                    "独立した設定のタブ構成");
                __targets.SelectedIndex = 0;
                Require(Selected(__targets).Single().Count == 1 && __columnButtons[__targets].Length == 10, "監視対象列");
                Require(__columnButtons[__inbox].Length == 5 && __columnButtons[__history].Length == 3, "未読と履歴の列構成");
                Require(!this.GetVisualDescendants().OfType<TabControl>().Any(), "メインに設定・履歴タブを出さない");
                __settingsTabs.SelectedIndex = 1;
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
                await CapturePreviewScreens(fixture);
                File.WriteAllText(report, "PASS: Avalonia window, scan, stale result suppression, save retry, large unread lookup and SQLite restart.");
            }
            catch (Exception error) { File.WriteAllText(report, error.ToString()); }
            finally { __closed = true; CloseChildWindows(); Close(); }
        };
    }

    // 実データに触れず、運用中に近い複数行と階層を全画面で撮影する。
    private async Task CapturePreviewScreens(string folder)
    {
        var now = DateTimeOffset.Now;
        const string root = @"C:\サンプル\共有";
        var design = new WatchItem
        {
            Path = Path.Combine(root, "設計資料"), IsFolder = true, Recursive = true, Unread = 4,
            LastChanged = now.AddMinutes(-2), LastChecked = now, LatestFileWriteAt = now.AddMinutes(-2), QueryMs = 154
        };
        var files = new (string Path, string Kind, int Minutes)[]
        {
            (@"図面\制御盤レイアウト.pdf", "更新", 2),
            (@"図面\配線図.dwg", "追加", 5),
            (@"議事録\定例会議.docx", "更新", 12),
            ("レビュー予定.xlsx", "消失", 27)
        };
        foreach (var (path, kind, mins) in files)
            design.PendingFiles[path] = new(path, kind, now.AddMinutes(-mins), now.AddMinutes(-mins));
        design.Inventory = new(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, _, _) in files.Take(3)) design.Inventory[path] = new(10, 20, 30);

        var sales = new WatchItem { Path = Path.Combine(root, "週次集計.xlsx"), Unread = 1, LastChange = "更新",
            Baseline = ProbeState.Found, LastChanged = now.AddMinutes(-34), LastChecked = now,
            LatestFileWriteAt = now.AddMinutes(-34), QueryMs = 18 };
        var notes = new WatchItem { Path = Path.Combine(root, "作業手順"), IsFolder = true,
            LastChecked = now, Inventory = new(StringComparer.OrdinalIgnoreCase) { ["手順書.pdf"] = new(10, 20, 30) }, QueryMs = 31 };
        var offline = new WatchItem { Path = Path.Combine(root, "保管庫"), IsFolder = true,
            LastChecked = now, Error = "共有フォルダに接続できません" };
        __state.Items.Clear();
        __state.Items.AddRange([design, sales, notes, offline]);
        __state.History.Clear();
        foreach (var (path, kind, mins) in files) __state.History.Enqueue(new(now.AddMinutes(-mins), Path.Combine(design.Path, path), kind));
        __state.History.Enqueue(new(now.AddMinutes(-34), sales.Path, "更新"));
        __state.ExcludedFilePatterns = ["~$*", "*.tmp"];
        __state.Paused = false;
        Refresh(true);

        Width = 940;
        Height = 510;
        ExpandAll();
        __inbox.SelectedIndex = 2;
        ToggleDetails();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        VerifyRowColors();
        SaveLayout(this, Path.Combine(folder, "01_Inbox.png"));

        __settingsWindow!.Width = 1510;
        __settingsWindow.Height = 480;
        __settingsTabs.SelectedIndex = 0;
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        __targets.SelectedIndex = 0;
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        SaveLayout(__settingsWindow, Path.Combine(folder, "02_Watch_Targets.png"));

        __settingsWindow.Width = 900;
        __settingsWindow.Height = 790;
        __settingsTabs.SelectedIndex = 1;
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        SaveLayout(__settingsWindow, Path.Combine(folder, "03_Behavior_and_Display.png"));

        __historyWindow!.Width = 980;
        __historyWindow.Height = 460;
        __history.SelectedIndex = 2;
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        SaveLayout(__historyWindow, Path.Combine(folder, "04_History.png"));
    }

    private void VerifyRowColors()
    {
        var surfaces = __inbox.GetVisualDescendants().OfType<TableRowSurface>().ToArray();
        Require(surfaces.Length >= 3, "複数の未読行を描画");
        Require(surfaces.All(x => x.BorderThickness.Bottom == 1 && x.BorderBrush is not null), "すべての行に区切り線");
        Require(surfaces.Take(3).Select(x => x.Background).Distinct().Count() == 3, "交互の行色と選択色を区別");

        // 選択変更やテーマ変更でも、行を作り直さずに色が追従する。
        var initialColor = surfaces[0].Background;
        __inbox.SelectedIndex = 0;
        Require(!Equals(initialColor, surfaces[0].Background), "選択変更で行色が切り替わる");
        var lightSelection = surfaces[0].Background;
        __state.DarkMode = true;
        ApplyAppearance();
        Require(!Equals(lightSelection, surfaces[0].Background), "ダークモードの配色を反映");
        __state.DarkMode = false;
        ApplyAppearance();
        __inbox.SelectedIndex = 2;
    }

    private static void SaveLayout(Window window, string path)
    {
        if (window.Content is not Control content || content.Bounds.Width < 1 || content.Bounds.Height < 1) return;
        using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(
            new PixelSize((int)Math.Ceiling(content.Bounds.Width), (int)Math.Ceiling(content.Bounds.Height)));
        bitmap.Render(content);
        bitmap.Save(path, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
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
