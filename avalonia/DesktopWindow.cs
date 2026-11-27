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
using Avalonia.VisualTree;

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
    private bool __inboxDirty = true, __targetsDirty = true, __historyDirty = true;
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
        __inbox.ItemTemplate = CreateRowTemplate(__inbox);
        __targets.ItemTemplate = CreateRowTemplate(__targets);
        __history.ItemTemplate = CreateRowTemplate(__history);
        __tabs.Items.Add(new TabItem { Header = "未読", Content = BuildInbox() });
        __tabs.Items.Add(new TabItem { Header = "監視対象", Content = BuildTargets() });
        __tabs.Items.Add(new TabItem { Header = "設定", Content = BuildPreferences() });
        __tabs.Items.Add(new TabItem { Header = "履歴", Content = BuildHistory() });
        __tabs.KeyDown += Keyboard;
        __tabs.SelectionChanged += (_, e) => { if (ReferenceEquals(e.Source, __tabs)) RefreshVisibleList(); };
        __inbox.SelectionChanged += (_, _) => __details.Text = Selected(__inbox).FirstOrDefault() is { } row
            ? $"{row.Path}  ｜  {row.Detail}  ｜  最終更新者：取得不可" : "";
        foreach (var list in new[] { __inbox, __targets, __history })
            list.DoubleTapped += async (_, e) =>
            {
                // 複数選択中も、クリックした行だけを開く。展開ボタンでは開かない。
                if (e.Source is not Control origin || origin.FindAncestorOfType<Button>(includeSelf: true) is not null) return;
                if (origin.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext is Row row)
                    await OpenRow(row, parent: false);
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
        __monitor.ProgressChanged += RefreshStatus;
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
            if (args.Property == WindowStateProperty && WindowState == WindowState.Minimized && __native.IsVisible && !__closing)
                Dispatcher.UIThread.Post(Hide);
        };
        Closing += (_, e) =>
        {
            if (__closed) return;
            e.Cancel = true;
            if (__closing) return;
            if (__native.IsVisible) { Hide(); return; }
            Dispatcher.UIThread.Post(async () => await CloseFully());
        };
        Closed += (_, _) => __native.Dispose();
    }

    private nint Handle => TryGetPlatformHandle()?.Handle ?? 0;

    // Avalonia は行を再利用するとき、一時的にデータがない状態でもテンプレートを呼ぶ。
    private FuncDataTemplate<Row> CreateRowTemplate(ListBox list) =>
        new((row, _) => row is null ? null : BuildRow(row, list), false);

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
        Button("すべて折りたたむ", CollapseAll),
        Button("確認済み", () => Acknowledge(Selected(__inbox))),
        Button("消失を消す", ClearDeleted)), __details);

    private void CollapseAll()
    {
        __expanded.Clear();
        RefreshInbox();
    }

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

    private void Refresh(bool unreadChanged)
    {
        __targetsDirty = true;
        __inboxDirty |= unreadChanged;
        __historyDirty |= unreadChanged;
        RefreshVisibleList();
        RefreshStatus();
    }

    // 非表示タブは切り替え時に作る。大量の履歴や未読があっても他の操作をブロックしない。
    private void RefreshVisibleList()
    {
        switch (__tabs.SelectedIndex)
        {
            case 0 when __inboxDirty: RefreshInbox(); break;
            case 1 when __targetsDirty: RefreshTargets(); break;
            case 3 when __historyDirty: RefreshHistory(); break;
        }
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
        catch (IOException error)
        {
            __closing = false;
            ShowFromTray();
            WinDesktop.Message("保存できなかったため終了していません。保存先を確認してから、もう一度終了してください。\n" + error.Message, Handle);
            return;
        }
        __closed = true;
        Close();
    }
}
