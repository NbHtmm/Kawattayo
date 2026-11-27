using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace ShareFileWatch;

internal sealed partial class DesktopWindow
{
    private readonly TabControl __settingsTabs = new();
    private readonly TextBlock __scope = new() { TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock __emptyInbox = new()
    {
        Text = "未読の変更はありません", IsHitTestVisible = false,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
    };
    private readonly TextBlock __registered = new() { VerticalAlignment = VerticalAlignment.Center };
    private Window? __settingsWindow, __historyWindow;
    private Button? __pin, __pause, __failures, __detailsToggle;

    // メイン画面は未読専用。設定と履歴は WinForms 版と同じく独立したウィンドウにする。
    private Control BuildMainPage()
    {
        __pin = Button("", TogglePinned);
        var commands = Bar(Button("↻ 更新", () => _ = __monitor.Scan()), __pin,
            Button("履歴", ShowHistory), Button("設定…", () => ShowSettings()), BuildToolsMenu());
        commands.HorizontalAlignment = HorizontalAlignment.Right;
        __summary.VerticalAlignment = VerticalAlignment.Center;
        var header = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(commands, Dock.Right);
        header.Children.Add(commands);
        header.Children.Add(__summary);

        __failures = Button("", () => ShowSettings(0));
        var status = new DockPanel { Margin = new Thickness(0, 3, 0, 0) };
        DockPanel.SetDock(__failures, Dock.Right);
        status.Children.Add(__failures);
        status.Children.Add(__status);
        __status.VerticalAlignment = VerticalAlignment.Center;
        __status.TextTrimming = TextTrimming.CharacterEllipsis;
        ToolTip.SetTip(__status, "");
        return DockPage(header, BuildInbox(), status);
    }

    private void TogglePinned()
    {
        __state.Pinned = !__state.Pinned;
        ApplyAppearance();
        __monitor.UpdateSettings();
    }

    private Control BuildInbox()
    {
        __detailsToggle = Button("詳細 ▾", ToggleDetails);
        var actions = Bar(Button("すべて展開", ExpandAll), Button("すべて折りたたむ", CollapseAll),
            Button("消失通知を消す", ClearDeleted), __detailsToggle);
        actions.HorizontalAlignment = HorizontalAlignment.Right;
        var lower = new StackPanel();
        lower.Children.Add(__scope);
        lower.Children.Add(actions);
        lower.Children.Add(__detailsBox);
        var list = new Grid();
        list.Children.Add(__inbox);
        list.Children.Add(__emptyInbox);
        return DockPage(BuildHeadings(__inbox), list, lower);
    }

    private static Control DockPage(Control? header, Control body, Control? footer = null)
    {
        var page = new DockPanel { Margin = new Thickness(8, 3) };
        if (header is not null)
        {
            DockPanel.SetDock(header, Dock.Top);
            page.Children.Add(header);
        }
        if (footer is not null)
        {
            DockPanel.SetDock(footer, Dock.Bottom);
            page.Children.Add(footer);
        }
        page.Children.Add(body);
        return page;
    }

    private void ShowSettings(int? tab = null)
    {
        if (__settingsWindow is null)
        {
            __settingsTabs.Items.Add(new TabItem { Header = "監視対象", Content = BuildTargets() });
            __settingsTabs.Items.Add(new TabItem { Header = "動作と表示", Content = BuildPreferences() });
            __settingsTabs.SelectionChanged += (_, e) =>
            {
                if (ReferenceEquals(e.Source, __settingsTabs)) RefreshVisibleList();
            };
            __settingsWindow = BuildChild("設定", __settingsTabs, 960, 530);
        }
        if (tab is int index) __settingsTabs.SelectedIndex = index;
        ShowChild(__settingsWindow);
    }

    private void ShowHistory()
    {
        __historyWindow ??= BuildChild("変更履歴", BuildHistory(), 870, 440);
        ShowChild(__historyWindow);
    }

    private Window BuildChild(string title, Control content, int width, int height)
    {
        var window = new Window
        {
            Title = $"{AppDefaults.AppName} | {title}", Icon = Icon, Content = content, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Width = width, Height = height, MinWidth = 380, MinHeight = 260, ShowInTaskbar = false
        };
        AddListStyle(window);
        // 非表示にすることで設定の入力や履歴の検索を再表示時にも維持する。
        window.Closing += (_, e) =>
        {
            if (__closed) return;
            e.Cancel = true;
            window.Hide();
        };
        return window;
    }

    private void ShowChild(Window window)
    {
        ApplyAppearance();
        if (!window.IsVisible) window.Show(this);
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
        RefreshVisibleList();
        RefreshStatus();
    }

    private static Control SelectionFooter(ListBox list, string empty = "")
    {
        var footer = new TextBox { IsReadOnly = true, Text = empty, Margin = new Thickness(0, 3, 0, 0) };
        list.SelectionChanged += (_, _) => footer.Text = Selected(list).FirstOrDefault() is { } row
            ? row.Path + (row.Target?.Error is { } error ? $"  ｜  {error}" : "") : empty;
        return footer;
    }

    private void HideToTray()
    {
        if (__settingsWindow?.IsVisible == true) __settingsWindow.Hide();
        if (__historyWindow?.IsVisible == true) __historyWindow.Hide();
        Hide();
    }

    private Control BuildToolsMenu()
    {
        var item = new MenuItem { Header = "差分収集…" };
        item.Click += (_, _) => ShowCollector();
        var button = new Button { Content = "ツール ▾", ContextMenu = new ContextMenu { Items = { item } } };
        button.Click += (_, _) => button.ContextMenu!.Open(button);
        return button;
    }

    private void CloseChildWindows()
    {
        __settingsWindow?.Close();
        __historyWindow?.Close();
        collector?.Close();
    }

    private Control BuildTargetToolbar()
    {
        var add = new Button { Content = "＋ 追加 ▾" };
        var menu = new ContextMenu();
        var folder = new MenuItem { Header = "フォルダを選択…" };
        folder.Click += (_, _) => _ = Pick(true);
        var file = new MenuItem { Header = "ファイルを選択…" };
        file.Click += (_, _) => _ = Pick(false);
        menu.Items.Add(folder);
        menu.Items.Add(file);
        add.Click += (_, _) => menu.Open(add);
        __pause = Button("照会を中断", __monitor.PauseOrResume);
        return Bar(add, __registered, __pause);
    }
}
