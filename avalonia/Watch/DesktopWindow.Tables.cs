using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace ShareFileWatch;

internal sealed partial class DesktopWindow
{
    private static readonly string[] InboxColumns = ["ファイル・フォルダ", "変更", "最終更新者", "最新の検知", "操作"];
    private static readonly string[] TargetColumns = ["名前", "対象ファイル数", "状態", "最終更新", "最終照会", "種類", "再帰監視", "変更通知", "照会時間", "完全なパス"];
    private static readonly string[] HistoryColumns = ["検知日時", "変更", "ファイルパス"];
    private readonly Dictionary<ListBox, Button[]> __columnButtons = new();

    // 一覧と見出しで幅を共有し、狭い画面では未読の名前欄を可変にする。
    private static Grid TableGrid(ListBox list)
    {
        string columns = list.Name switch
        {
            "Targets" => "190,105,130,150,150,74,90,140,85,*",
            "History" => "150,66,*",
            _ => "*,76,82,100,70"
        };
        return new Grid { ColumnDefinitions = new(columns) };
    }

    private Control BuildHeadings(ListBox list)
    {
        var table = TableGrid(list);
        string[] names = list == __targets ? TargetColumns : list == __history ? HistoryColumns : InboxColumns;
        var buttons = new Button[names.Length];
        for (int i = 0; i < names.Length; ++i)
        {
            int column = i;
            var heading = Button(names[i], () => Sort(list, column));
            heading.HorizontalAlignment = HorizontalAlignment.Stretch;
            heading.Padding = new Thickness(4, 3);
            heading.IsEnabled = list != __inbox || i is 0 or 1 or 3;
            ToolTip.SetTip(heading, names[i]);
            Grid.SetColumn(heading, i);
            table.Children.Add(heading);
            buttons[i] = heading;
        }
        __columnButtons[list] = buttons;
        UpdateHeadings(list);
        return new Border { Padding = new Thickness(7, 0, 4, 0), Child = table };
    }

    private void UpdateHeadings(ListBox list)
    {
        if (!__columnButtons.TryGetValue(list, out var buttons)) return;
        var (column, reverse, titles) = list == __targets ? (__targetSort, __targetReverse, TargetColumns)
            : list == __history ? (__historySort, __historyReverse, HistoryColumns) : (__inboxSort, __inboxReverse, InboxColumns);
        for (int i = 0; i < buttons.Length; ++i)
            buttons[i].Content = i == column ? titles[i] + (reverse ? " ▾" : " ▴") : titles[i];
    }

    private static void AddCell(Grid grid, int index, string? value)
    {
        var cell = new TextBlock { Text = value ?? "—", TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0) };
        ToolTip.SetTip(cell, cell.Text);
        Grid.SetColumn(cell, index);
        grid.Children.Add(cell);
    }

    internal static string TimeCell(DateTimeOffset? time) => time?.ToLocalTime().ToString("yyyy/MM/dd HH:mm:ss") ?? "—";

    private Control BuildTableRow(Row row, ListBox list)
    {
        var line = TableGrid(list);
        line.MinHeight = Math.Max(27, FontSize * 1.65);
        if (list == __history)
        {
            AddCell(line, 0, TimeCell(row.At));
            AddCell(line, 1, row.Detail);
            AddCell(line, 2, row.Path);
            return line;
        }

        line.Children.Add(BuildNameCell(row));
        if (list == __targets && row.TargetInfo is { } info)
        {
            AddCell(line, 1, row.Count?.ToString("N0"));
            AddCell(line, 2, info.State);
            AddCell(line, 3, TimeCell(row.At));
            AddCell(line, 4, TimeCell(info.Checked));
            AddCell(line, 5, row.Folder ? "フォルダ" : "ファイル");
            AddCell(line, 6, info.Recursive);
            AddCell(line, 7, info.Feed);
            AddCell(line, 8, info.Elapsed);
            AddCell(line, 9, row.Path);
            return line;
        }

        AddCell(line, 1, InboxKind(row));
        AddCell(line, 2, "取得不可");
        AddCell(line, 3, Age(row.At));
        var read = Button("既読", () => Acknowledge([row]));
        read.Padding = new Thickness(7, 1);
        Avalonia.Automation.AutomationProperties.SetName(read, $"{row.Name} を既読にする");
        Grid.SetColumn(read, 4);
        line.Children.Add(read);
        return line;
    }

    private Control BuildNameCell(Row row)
    {
        // インデントは上限を設け、深い階層でも名前を表示する余地を残す。
        var name = new DockPanel { Margin = new Thickness(Math.Min(row.Depth, 5) * 10, 0, 3, 0), ClipToBounds = true };
        var marker = row.Branch is { } branch ? Button(row.Expanded ? "−" : "+", () => Toggle(branch))
            : new Button { IsVisible = false };
        marker.Padding = new Thickness(3, 0);
        marker.Focusable = false;
        Avalonia.Automation.AutomationProperties.SetName(marker, row.Expanded ? "折りたたむ" : "展開する");
        var markerSlot = new Grid { Width = Math.Max(22, FontSize + 8), VerticalAlignment = VerticalAlignment.Center };
        markerSlot.Children.Add(marker);
        DockPanel.SetDock(markerSlot, Dock.Left);
        name.Children.Add(markerSlot);
        var icon = new TextBlock { Text = row.Folder ? "📁" : "📄",
            Margin = new Thickness(1, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(icon, Dock.Left);
        name.Children.Add(icon);
        name.Children.Add(new TextBlock { Text = row.Name, FontWeight = row.Folder ? FontWeight.SemiBold : FontWeight.Normal, TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center });
        ToolTip.SetTip(name, $"{row.Name}\n{row.Path}\n{row.Detail}");
        return name;
    }

    internal static string InboxKind(Row row) => row.Change?.Kind ??
        (row.Folder ? $"{row.Count:N0} 件" : row.Target?.LastChange ?? "更新");
}
