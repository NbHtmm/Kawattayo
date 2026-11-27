using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Styling;

namespace ShareFileWatch;

// 設定項目の表示と、監視セッションへの設定反映をまとめる。
internal sealed partial class DesktopWindow
{
    private Control BuildPreferences()
    {
        var body = new StackPanel { Spacing = 10, Margin = new Thickness(2, 8) };
        var seconds = new NumericUpDown { Minimum = AppDefaults.MinimumPollSeconds, Maximum = AppDefaults.MaximumPollSeconds, Increment = 5, Value = __state.PollSeconds, Width = 125 };
        seconds.ValueChanged += (_, _) => { if (seconds.Value is { } v) { __monitor.ApplyPollInterval((int)v); } };
        var size = new NumericUpDown { Minimum = 9, Maximum = 22, Value = Math.Clamp(__state.TextSizePoints, 9, 22), Width = 125 };
        size.ValueChanged += (_, _) => { if (size.Value is { } v) { __state.TextSizePoints = (int)v; ApplyAppearance(); __monitor.UpdateSettings(); } };
        body.Children.Add(Bar(new TextBlock { Text = "監視間隔（秒）", VerticalAlignment = VerticalAlignment.Center }, seconds));
        body.Children.Add(Bar(new TextBlock { Text = "文字サイズ（pt）", VerticalAlignment = VerticalAlignment.Center }, size));
        body.Children.Add(Bar(Button("照会を中断 / 再開", __monitor.PauseOrResume)));
        body.Children.Add(Option("通知領域に常駐", __state.KeepInTray, v => { __state.KeepInTray = v; __native.SetVisible(v); }));
        body.Children.Add(Option("常に最前面", __state.Pinned, v => { __state.Pinned = v; Topmost = v; }));
        body.Children.Add(Option("ダークモード", __state.DarkMode, v => { __state.DarkMode = v; ApplyAppearance(); }));
        body.Children.Add(Option("サーバー負荷を抑える", __state.LowServerLoad, v => __state.LowServerLoad = v, true));
        body.Children.Add(Option("登録フォルダ自身の更新日時も監視", __state.WatchDirectoryTime, v =>
        {
            __state.WatchDirectoryTime = v;
            // オンにした最初の照会をフォルダ更新として通知しない。
            foreach (var item in __state.Items) item.DirectoryStamp = null;
        }, true));
        body.Children.Add(Option("フォルダを再帰監視する（既定）", __state.RecursiveFolders != false, v =>
        {
            __state.RecursiveFolders = v;
            foreach (var item in __state.Items.Where(i => i.IsFolder && i.RecursiveOverride is null)) item.SetRecursive(v);
        }, true));
        body.Children.Add(Option("ファイルの削除を通知する", __state.NotifyDeleted, v => __state.NotifyDeleted = v));
        body.Children.Add(Option("ファイル・フォルダを開いたら既読にする", __state.ReadOnFileOpen, v => __state.ReadOnFileOpen = v));
        body.Children.Add(Option("未読のファイルも更新のたびに通知する", __state.RenotifyUnread, v => __state.RenotifyUnread = v));
        body.Children.Add(new TextBlock { Text = "除外するファイル名（1行に1つ、* と ? が使えます）" });
        var rules = new TextBox { AcceptsReturn = true, MinHeight = 74, Text = string.Join(Environment.NewLine, __state.ExcludedFilePatterns) };
        body.Children.Add(rules);
        body.Children.Add(Bar(Button("除外ルールを適用", () => SetExclusions(rules.Text ?? "")),
            Button("Officeの一時ファイルを追加", () =>
            {
                var text = rules.Text ?? "";
                if (!text.Split('\n').Any(x => x.Trim() == "~$*")) rules.Text = text.TrimEnd() + Environment.NewLine + "~$*";
            })));
        return new ScrollViewer { Content = body };
    }

    private CheckBox Option(string title, bool set, Action<bool> changed, bool scan = false)
    {
        var box = new CheckBox { Content = title, IsChecked = set };
        box.IsCheckedChanged += (_, _) => { changed(box.IsChecked == true); __monitor.UpdateSettings(scan); };
        return box;
    }

    private void ApplyAppearance()
    {
        RequestedThemeVariant = __state.DarkMode ? ThemeVariant.Dark : ThemeVariant.Light;
        FontSize = Math.Clamp(__state.TextSizePoints, 9, 22) * 4.0 / 3;
        Topmost = __state.Pinned;
    }
}
