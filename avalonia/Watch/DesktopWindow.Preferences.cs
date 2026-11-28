using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;

namespace ShareFileWatch;

// 設定の区分は WinForms 版に合わせ、各変更は同じ監視セッションに反映する。
internal sealed partial class DesktopWindow
{
    private Button? __pauseOption;

    private Control BuildPreferences()
    {
        var body = new StackPanel { Spacing = 8, Margin = new Thickness(10, 6) };
        body.Children.Add(BuildMonitoringOptions());
        body.Children.Add(BuildNotificationOptions());
        body.Children.Add(BuildDisplayOptions());
        body.Children.Add(BuildExclusionOptions());
        return new ScrollViewer { Content = body };
    }

    private Control BuildMonitoringOptions()
    {
        var seconds = new NumericUpDown
        {
            Minimum = AppDefaults.MinimumPollSeconds, Maximum = AppDefaults.MaximumPollSeconds, Increment = 5,
            Value = __state.PollSeconds, Width = 125
        };
        seconds.ValueChanged += (_, _) => { if (seconds.Value is { } value) __monitor.ApplyPollInterval((int)value); };
        __pauseOption = Button("一時停止", __monitor.PauseOrResume);
        return OptionGroup("監視",
            Bar(SettingLabel("監視間隔（秒）"), seconds, __pauseOption),
            Option("デフォルトでサブフォルダも再帰的に監視", __state.RecursiveFolders != false, v =>
            {
                __state.RecursiveFolders = v;
                foreach (var item in __state.Items.Where(i => i.IsFolder && i.RecursiveOverride is null)) item.SetRecursive(v);
            }, true),
            Option("登録フォルダ自身の更新日時も監視", __state.WatchDirectoryTime, v =>
            {
                __state.WatchDirectoryTime = v;
                // 初回の照会をフォルダの更新として通知しない。
                foreach (var item in __state.Items.Where(i => i.IsFolder)) item.DirectoryStamp = null;
            }, true),
            Option("サーバー負荷を抑える（変更通知を優先）", __state.LowServerLoad, v => __state.LowServerLoad = v, true));
    }

    private Control BuildNotificationOptions()
    {
        var seconds = new NumericUpDown { Minimum = 1, Maximum = 300, Value = __state.StableSeconds, Width = 125 };
        seconds.ValueChanged += (_, _) =>
        {
            if (seconds.Value is not { } value) return;
            __state.StableSeconds = (int)value;
            __monitor.UpdateSettings();
        };
        return OptionGroup("通知と既読",
        Option("通知領域に常駐", __state.KeepInTray, v => { __state.KeepInTray = v; __native.SetVisible(v); }),
        Option("ファイル・フォルダを開いたら既読にする", __state.ReadOnFileOpen, v => __state.ReadOnFileOpen = v),
        Option("未読のファイルも更新のたびに通知する", __state.RenotifyUnread, v => __state.RenotifyUnread = v),
        Option("ファイルの削除（消失）を通知", __state.NotifyDeleted, v => __state.NotifyDeleted = v),
        Option("既定でファイルの更新が落ち着いてから通知する", __state.WaitForStable, v => __state.WaitForStable = v),
        Bar(SettingLabel("安定待ち（秒）"), seconds),
        new TextBlock { Text = "サイズと更新日時を巡回ごとに比較します。未読一覧にはすぐ表示されます。",
            TextWrapping = TextWrapping.Wrap });
    }

    private Control BuildDisplayOptions()
    {
        var size = new NumericUpDown
        {
            Minimum = 9, Maximum = 22, Value = Math.Clamp(__state.TextSizePoints, 9, 22), Width = 125
        };
        size.ValueChanged += (_, _) =>
        {
            if (size.Value is not { } value) return;
            __state.TextSizePoints = (int)value;
            ApplyAppearance();
            __monitor.UpdateSettings();
        };
        return OptionGroup("表示",
            Option("ダークモード", __state.DarkMode, v => { __state.DarkMode = v; ApplyAppearance(); }),
            Bar(SettingLabel("文字サイズ（pt）"), size));
    }

    private Control BuildExclusionOptions()
    {
        var rules = new TextBox { AcceptsReturn = true, MinHeight = 74, Text = string.Join(Environment.NewLine, __state.ExcludedFilePatterns) };
        return OptionGroup("除外するファイル", new TextBlock
        {
            Text = "1行に1つ指定します。* と ? が使えます。",
            TextWrapping = TextWrapping.Wrap
        }, rules, Bar(Button("除外ルールを適用", () => SetExclusions(rules.Text ?? "")),
            Button("Officeの一時ファイルを追加", () =>
            {
                if (AppendOfficeExclusion(rules.Text) is { } updated) rules.Text = updated;
            })));
    }

    // すでに登録済みの場合は null を返し、編集中の空白や改行を変更しない。
    internal static string? AppendOfficeExclusion(string? input)
    {
        var text = input?.TrimEnd() ?? "";
        if (text.Split('\n').Any(x => x.Trim() == "~$*")) return null;
        return text.Length == 0 ? "~$*" : text + Environment.NewLine + "~$*";
    }

    private static TextBlock SettingLabel(string text) => new() { Text = text, VerticalAlignment = VerticalAlignment.Center };

    private static Control OptionGroup(string title, params Control[] controls)
    {
        var group = new StackPanel { Spacing = 2 };
        group.Children.Add(new TextBlock { Text = title, FontWeight = FontWeight.SemiBold });
        foreach (var control in controls) group.Children.Add(control);
        return new Border { BorderThickness = new Thickness(1), BorderBrush = Brushes.Gray,
            CornerRadius = new CornerRadius(4), Padding = new Thickness(9, 6), Child = group };
    }

    private CheckBox Option(string title, bool set, Action<bool> changed, bool scan = false)
    {
        var box = new CheckBox { Content = title, IsChecked = set };
        box.IsCheckedChanged += (_, _) => { changed(box.IsChecked == true); __monitor.UpdateSettings(scan); };
        return box;
    }

    private static void ApplyWindowBackground(Window window, IBrush background)
    {
        window.Background = background;
        if (window.Content is Panel panel) panel.Background = background;
        else if (window.Content is TabControl tabs) tabs.Background = background;
    }

    private void ApplyAppearance()
    {
        RequestedThemeVariant = __state.DarkMode ? ThemeVariant.Dark : ThemeVariant.Light;
        FontSize = Math.Clamp(__state.TextSizePoints, 9, 22) * 4.0 / 3;
        Topmost = __state.Pinned;
        var background = new SolidColorBrush(Color.Parse(__state.DarkMode ? "#20242B" : "#F5F6F8"));
        ApplyWindowBackground(this, background);
        foreach (var child in new[] { __settingsWindow, __historyWindow })
        {
            if (child is null) continue;
            child.RequestedThemeVariant = RequestedThemeVariant;
            child.FontSize = FontSize;
            ApplyWindowBackground(child, background);
        }
    }
}
