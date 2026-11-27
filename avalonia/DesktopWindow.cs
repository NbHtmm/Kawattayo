using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;

namespace ShareFileWatch;

internal sealed partial class DesktopWindow : Window
{
    private readonly MonitorSession __monitor;
    private readonly AppState __state;
    private readonly WinDesktop __native;
    private readonly ListBox __inbox = NewList(), __targets = NewList(), __history = NewList();
    private readonly TabControl __tabs = new();
    private readonly TextBlock __status = new(), __summary = new(), __details = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly TextBox __filter = new() { PlaceholderText = "履歴を検索", Width = 220 };
    private readonly HashSet<string> __expanded = new(StringComparer.OrdinalIgnoreCase);
    private bool __closing, __closed;
    private int __inboxSort, __targetSort, __historySort = 1;
    private bool __inboxReverse, __targetReverse, __historyReverse = true;

    private sealed record Row(string Key, WatchItem? Target, string Name, string Detail, DateTimeOffset? At,
        int? Count = null, string? Path = null, FileChange? Change = null, bool Folder = false, int Depth = 0,
        string? Branch = null, bool Expanded = false, bool Root = false);

    internal DesktopWindow(MonitorSession monitor)
    {
        __monitor = monitor;
        __state = monitor.State;
        __native = new(this, ShowFromTray, () => _ = __monitor.Scan(), RequestExit);
        Title = AppDefaults.WindowTitle + " · Avalonia";
        Width = 850;
        Height = 560;
        MinWidth = 510;
        MinHeight = 340;
        using (var stream = GetType().Assembly.GetManifestResourceStream("folder-bell.ico"))
            if (stream is not null) Icon = new WindowIcon(stream);
        __inbox.ItemTemplate = new FuncDataTemplate<Row>((row, _) => BuildRow(row, __inbox), false);
        __targets.ItemTemplate = new FuncDataTemplate<Row>((row, _) => BuildRow(row, __targets), false);
        __history.ItemTemplate = new FuncDataTemplate<Row>((row, _) => BuildRow(row, __history), false);
        __tabs.Items.Add(new TabItem { Header = "未読", Content = BuildInbox() });
        __tabs.Items.Add(new TabItem { Header = "監視対象", Content = BuildTargets() });
        __tabs.Items.Add(new TabItem { Header = "設定", Content = BuildPreferences() });
        __tabs.Items.Add(new TabItem { Header = "履歴", Content = BuildHistory() });
        __tabs.KeyDown += Keyboard;
        __inbox.SelectionChanged += (_, _) => __details.Text = Selected(__inbox).FirstOrDefault() is { } row
            ? $"{row.Path}  ｜  {row.Detail}  ｜  最終更新者：取得不可" : "";
        foreach (var list in new[] { __inbox, __targets, __history })
            list.DoubleTapped += async (_, e) =>
            {
                if (e.Source is Button) return;
                if (Selected(list).FirstOrDefault() is { } row) await OpenRow(row, parent: false);
            };
        var page = new DockPanel { Margin = new Thickness(14, 10) };
        var header = new Grid { ColumnDefinitions = new("*,Auto"), Margin = new Thickness(0, 0, 0, 10) };
        header.Children.Add(__summary);
        var scan = Button("↻ 今すぐ照会", () => _ = __monitor.Scan()); Grid.SetColumn(scan, 1); header.Children.Add(scan);
        DockPanel.SetDock(header, Dock.Top); page.Children.Add(header);
        DockPanel.SetDock(__status, Dock.Bottom);
        __status.Margin = new Thickness(4, 8, 4, 0); page.Children.Add(__status);
        page.Children.Add(__tabs);
        Content = page;
        Styles.Add(new Style(x => x.OfType<ListBoxItem>()) { Setters = { new Setter(ContentControl.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch) } });
        ApplyAppearance();
        __monitor.RowsChanged += Refresh;
        __monitor.ProgressChanged += () => { RefreshStatus(); if (__tabs.SelectedIndex == 1) RefreshTargets(); };
        __monitor.Alerts += __native.Notify;
        Opened += (_, _) =>
        {
            __native.Attach();
            __native.SetVisible(__state.KeepInTray);
            Refresh(true);
            __monitor.StartTimer(immediate: true);
            if (__monitor.StartupWarning is { } warning) WinDesktop.Message(warning, Handle);
        };
        PropertyChanged += (_, args) =>
        {
            if (args.Property == WindowStateProperty && WindowState == WindowState.Minimized && __state.KeepInTray && !__closing)
                Dispatcher.UIThread.Post(Hide);
        };
        Closing += (_, e) =>
        {
            if (__closed) return;
            e.Cancel = true;
            if (__closing) return;
            if (__state.KeepInTray) { Hide(); return; }
            Dispatcher.UIThread.Post(async () => await CloseFully());
        };
        Closed += (_, _) => __native.Dispose();
    }

    private nint Handle => TryGetPlatformHandle()?.Handle ?? 0;

    private static ListBox NewList() => new() { SelectionMode = SelectionMode.Multiple };
    private static Button Button(string text, Action action)
    {
        var button = new Button { Content = text, Padding = new Thickness(9, 4) };
        button.Click += (_, _) => action();
        return button;
    }
    private static WrapPanel Bar(params Control[] controls)
    {
        var panel = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 5) };
        foreach (var control in controls) { control.Margin = new Thickness(0, 0, 6, 3); panel.Children.Add(control); }
        return panel;
    }
    private static Control Panel(ListBox list, WrapPanel toolbar, Control? footer = null)
    {
        var pane = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
        DockPanel.SetDock(toolbar, Dock.Top); pane.Children.Add(toolbar);
        if (footer is not null) { DockPanel.SetDock(footer, Dock.Bottom); pane.Children.Add(footer); }
        pane.Children.Add(list);
        return pane;
    }

    private Control BuildInbox() => Panel(__inbox, Bar(
        Button("名前", () => Sort(__inbox, 0)),
        Button("更新", () => Sort(__inbox, 1)),
        Button("すべて展開", ExpandAll),
        Button("折りたたむ", () => { __expanded.Clear(); RefreshInbox(); }),
        Button("確認済み", () => Acknowledge(Selected(__inbox))),
        Button("消失を消す", ClearDeleted)), __details);

    private Control BuildTargets()
    {
        var page = Panel(__targets, Bar(
            Button("ファイル追加", () => _ = Pick(false)), Button("フォルダ追加", () => _ = Pick(true)),
            Button("名前", () => Sort(__targets, 0)), Button("最終更新", () => Sort(__targets, 1)),
            Button("対象ファイル数", () => Sort(__targets, 2))),
            new TextBlock { Text = "エクスプローラーからドロップして登録。右クリックで監視先ごとの操作。", Margin = new Thickness(6, 4) });
        DragDrop.SetAllowDrop(__targets, true);
        DragDrop.AddDragOverHandler(__targets, (_, e) => e.DragEffects =
            e.DataTransfer.Formats.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None);
        DragDrop.AddDropHandler(__targets, async (_, e) =>
        {
            var paths = e.DataTransfer.TryGetFiles()?.Select(x => x.TryGetLocalPath()).OfType<string>().ToArray();
            if (paths is { Length: > 0 }) await Register(paths);
        });
        return page;
    }

    private Control BuildHistory()
    {
        __filter.TextChanged += (_, _) => RefreshHistory();
        return Panel(__history, Bar(__filter, Button("名前", () => Sort(__history, 0)), Button("検知時刻", () => Sort(__history, 1))));
    }

    private Control BuildPreferences()
    {
        var body = new StackPanel { Spacing = 10, Margin = new Thickness(2, 8) };
        var seconds = new NumericUpDown { Minimum = AppDefaults.MinimumPollSeconds, Maximum = AppDefaults.MaximumPollSeconds, Increment = 5, Value = __state.PollSeconds, Width = 125 };
        seconds.ValueChanged += (_, _) => { if (seconds.Value is { } v) { __state.PollSeconds = (int)v; __monitor.UpdateSettings(); } };
        var size = new NumericUpDown { Minimum = 9, Maximum = 22, Value = Math.Clamp(__state.TextSizePoints, 9, 22), Width = 125 };
        size.ValueChanged += (_, _) => { if (size.Value is { } v) { __state.TextSizePoints = (int)v; ApplyAppearance(); __monitor.UpdateSettings(); } };
        body.Children.Add(Bar(new TextBlock { Text = "監視間隔（秒）", VerticalAlignment = VerticalAlignment.Center }, seconds));
        body.Children.Add(Bar(new TextBlock { Text = "文字サイズ（pt）", VerticalAlignment = VerticalAlignment.Center }, size));
        body.Children.Add(Bar(Button("照会を中断 / 再開", __monitor.PauseOrResume)));
        body.Children.Add(Option("通知領域に常駐", __state.KeepInTray, v => { __state.KeepInTray = v; __native.SetVisible(v); }));
        body.Children.Add(Option("常に最前面", __state.Pinned, v => { __state.Pinned = v; Topmost = v; }));
        body.Children.Add(Option("ダークモード", __state.DarkMode, v => { __state.DarkMode = v; ApplyAppearance(); }));
        body.Children.Add(Option("サーバー負荷を抑える", __state.LowServerLoad, v => { __state.LowServerLoad = v; __monitor.ResetQuietScan(); }, true));
        body.Children.Add(Option("登録フォルダ自身の更新日時も監視", __state.WatchDirectoryTime, v => { __state.WatchDirectoryTime = v; __monitor.ResetQuietScan(); }, true));
        body.Children.Add(Option("フォルダを再帰監視する（既定）", __state.RecursiveFolders != false, v =>
        {
            __state.RecursiveFolders = v;
            foreach (var item in __state.Items.Where(i => i.IsFolder && i.RecursiveOverride is null)) item.SetRecursive(v);
            __monitor.ResetQuietScan();
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

    private void Refresh(bool full)
    {
        RefreshTargets();
        if (full) { RefreshInbox(); RefreshHistory(); }
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        __summary.Text = $"未読 {__state.Items.Sum(x => (long)x.Unread):N0}  ｜  監視対象 {__state.Items.Count:N0}";
        int unavailable = __state.Items.Count(item => item.Error is not null);
        __status.Text = unavailable > 0 ? $"{__monitor.Status}  ｜  監視失敗 {unavailable}件（監視対象を確認）" : __monitor.Status;
    }

    private void ShowFromTray() { Show(); WindowState = WindowState.Normal; Activate(); }
    private async void RequestExit() => await CloseFully();
    private async Task CloseFully()
    {
        if (__closing) return;
        __closing = true;
        try { await __monitor.Close(); }
        catch (IOException error) { WinDesktop.Message(error.Message, Handle); }
        __closed = true;
        Close();
    }
}
