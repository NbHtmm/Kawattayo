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
    private readonly TextBlock __status = new(), __summary = new();
    private readonly TextBox __detailsBox = new()
    {
        IsVisible = false, IsReadOnly = true, AcceptsReturn = true, MaxHeight = 96,
        TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0)
    };
    private readonly TextBox __filter = new() { PlaceholderText = "履歴を検索", Width = 220 };
    private readonly HashSet<string> __expanded = new(StringComparer.OrdinalIgnoreCase);
    private bool __closing, __closed;
    private bool __inboxDirty = true, __targetsDirty = true, __historyDirty = true;
    private int __inboxSort, __targetSort, __historySort;
    private bool __inboxReverse, __targetReverse, __historyReverse = true;

    private sealed record Row(string Key, WatchItem? Target, string Name, string Detail, DateTimeOffset? At,
        int? Count = null, string? Path = null, FileChange? Change = null, bool Folder = false, int Depth = 0,
        string? Branch = null, bool Expanded = false, bool Root = false, TargetRowInfo? TargetInfo = null);

    private sealed record TargetRowInfo(string State, DateTimeOffset? Checked, string Recursive, string Feed, string Elapsed, long? ElapsedMs);

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
        __inbox.Name = "Inbox";
        __targets.Name = "Targets";
        __history.Name = "History";
        __inbox.ItemTemplate = CreateRowTemplate(__inbox);
        __targets.ItemTemplate = CreateRowTemplate(__targets);
        __history.ItemTemplate = CreateRowTemplate(__history);
        KeyDown += MainKeyboard;
        __inbox.SelectionChanged += (_, _) => RefreshDetails();
        foreach (var list in new[] { __inbox, __targets, __history })
        {
            list.KeyDown += Keyboard;
            list.DoubleTapped += async (_, e) =>
            {
                // 複数選択中も、クリックした行だけを開く。展開ボタンでは開かない。
                if (e.Source is not Control origin || origin.FindAncestorOfType<Button>(includeSelf: true) is not null) return;
                if (origin.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext is Row row)
                    await OpenRow(row, parent: false);
            };
        }
        Content = BuildMainPage();
        AddListStyle(this);
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
                Dispatcher.UIThread.Post(HideToTray);
        };
        Closing += (_, e) =>
        {
            if (__closed) return;
            e.Cancel = true;
            if (__closing) return;
            if (__native.IsVisible) { HideToTray(); return; }
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
    private static void AddListStyle(Window window) =>
        window.Styles.Add(new Style(x => x.OfType<ListBoxItem>())
        {
            Setters = { new Setter(ContentControl.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch),
                new Setter(TemplatedControl.PaddingProperty, new Thickness(4, 1)) }
        });
    private void ToggleDetails()
    {
        __detailsBox.IsVisible = !__detailsBox.IsVisible;
        if (__detailsToggle is not null) __detailsToggle.Content = __detailsBox.IsVisible ? "詳細 ▴" : "詳細 ▾";
        RefreshDetails();
    }

    private void RefreshDetails()
    {
        var rows = Selected(__inbox);
        __scope.Text = rows.Length == 0 ? "" : $"選択 {rows.Length:N0} 件";
        if (!__detailsBox.IsVisible) return;
        if (rows.Length == 0)
        {
            __detailsBox.Text = "未読一覧から項目を選んでください。";
            return;
        }
        var row = rows[0];
        string detectedAt = TimeCell(row.At);
        string modifiedAt = TimeCell(row.Change?.ModifiedAt ?? (row.Root ? row.Target?.LatestFileWriteAt : null));
        string scope = !row.Folder ? "このファイル" : row.Root ? "この監視対象の未読すべて" : "このフォルダ配下の未読";
        __detailsBox.Text = $"{row.Name} ｜ {row.Detail}\n更新時刻：{modifiedAt} ｜ 検知時刻：{detectedAt}\n"
            + $"確認済みの範囲：{scope}" + (rows.Length > 1 ? $"（先頭の選択項目。全 {rows.Length} 件を選択中）" : "")
            + $"\nパス：{row.Path}";
    }

    private void CollapseAll()
    {
        __expanded.Clear();
        RefreshInbox();
    }

    private Control BuildTargets()
    {
        var grid = DockPage(BuildHeadings(__targets), __targets);
        grid.MinWidth = 1300;
        var scroll = new ScrollViewer { Content = grid, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var page = DockPage(BuildTargetToolbar(), scroll,
            SelectionFooter(__targets, "エクスプローラーからドロップして登録できます。"));
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
        return DockPage(Bar(__filter), DockPage(BuildHeadings(__history), __history), SelectionFooter(__history));
    }

    private void Refresh(bool unreadChanged)
    {
        __targetsDirty = true;
        __inboxDirty |= unreadChanged;
        __historyDirty |= unreadChanged;
        RefreshVisibleList();
        RefreshStatus();
    }

    // 読み込み量の多い履歴・監視対象は表示されているときだけ更新する。
    private void RefreshVisibleList()
    {
        if (__inboxDirty) RefreshInbox();
        if (__targetsDirty && __settingsWindow?.IsVisible == true && __settingsTabs.SelectedIndex == 0) RefreshTargets();
        if (__historyDirty && __historyWindow?.IsVisible == true) RefreshHistory();
    }

    private void RefreshStatus()
    {
        __summary.Text = $"未読 {__state.Items.Sum(x => (long)x.Unread):N0} 件";
        __registered.Text = $"登録 {__state.Items.Count:N0} 件";
        if (__pin is not null) __pin.Content = __state.Pinned ? "最前面 ON" : "最前面 OFF";
        if (__pause is not null) __pause.Content = __state.Paused ? "監視を再開" : "照会を中断";
        if (__pauseOption is not null) __pauseOption.Content = __state.Paused ? "監視を再開" : "一時停止";
        int unavailable = __state.Items.Count(item => item.Error is not null);
        if (__failures is not null)
        {
            __failures.IsVisible = unavailable > 0;
            __failures.Content = $"監視失敗 {unavailable}件 ›";
        }
        __status.Text = __monitor.Status;
        ToolTip.SetTip(__status, __status.Text);
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
        CloseChildWindows();
        Close();
    }
}
