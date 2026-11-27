using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;

namespace ShareFileWatch;

internal sealed partial class DesktopWindow
{
    private static Row[] Selected(ListBox list) => list.SelectedItems?.OfType<Row>().ToArray() ?? [];
    private static string Label(WatchItem item) => string.IsNullOrWhiteSpace(item.DisplayName)
        ? Leaf(item.Path) : item.DisplayName;
    private static string Leaf(string path) => Path.GetFileName(Path.TrimEndingDirectorySeparator(path)) is { Length: > 0 } name ? name : path;
    private static string Age(DateTimeOffset? at)
    {
        if (at is null) return "—";
        var d = DateTimeOffset.Now - at.Value;
        if (d < TimeSpan.FromMinutes(1)) return "たった今";
        if (d < TimeSpan.FromHours(1)) return $"{(int)d.TotalMinutes}分前";
        if (d < TimeSpan.FromDays(1)) return $"{(int)d.TotalHours}時間前";
        return $"{at:MM/dd HH:mm}";
    }

    private Control BuildRow(Row row, ListBox source)
    {
        var line = new TableRowSurface(source, BuildTableRow(row, source));
        // 通常の一覧表示ではメニューを作らず、右クリックやメニューキーで必要になった時だけ組み立てる。
        line.ContextRequested += (_, e) =>
        {
            BuildRowMenu(row, source).Open(line);
            e.Handled = true;
        };
        line.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(line).Properties.IsRightButtonPressed && !Selected(source).Any(r => r.Key == row.Key))
                source.SelectedItem = row;
        };
        return line;
    }

    private ContextMenu BuildRowMenu(Row row, ListBox list)
    {
        var menu = new ContextMenu();
        void Add(string label, Action act)
        {
            var item = new MenuItem { Header = label };
            item.Click += (_, _) => act();
            menu.Items.Add(item);
        }
        Row[] SelectionOrRow() => Selected(list) is { Length: > 0 } selected && selected.Any(x => x.Key == row.Key) ? selected : [row];
        Add("開く", () => _ = OpenRow(row, false));
        if (!row.Folder) Add("親フォルダを開く", () => _ = OpenRow(row, true));
        Add("パスをコピー", () => _ = Copy(SelectionOrRow(), true));
        Add("コピー", () => _ = Copy(SelectionOrRow(), false));
        if (row.Target is { } target)
        {
            Add("確認済みにする", () => Acknowledge(SelectionOrRow()));
            if (row.Root)
            {
                Add("表示名を変更", () => _ = Rename(target));
                if (list == __targets)
                {
                    bool wait = target.WaitForStableOverride ?? __state.WaitForStable;
                    Add($"安定待ち：{(wait ? "オン" : "オフ")} → 切り替え", () => __monitor.SetTargetNotificationWait(target, !wait));
                    if (target.WaitForStableOverride is not null)
                        Add("安定待ち：既定に戻す", () => __monitor.SetTargetNotificationWait(target, null));
                }
                Add("更新フック…", () => _ = ConfigureHook(target));
                if (target.IsFolder)
                {
                    Add("再帰監視：オン", () => __monitor.SetTargetRecursion(target, true));
                    Add("再帰監視：オフ", () => __monitor.SetTargetRecursion(target, false));
                    Add("再帰監視：既定に戻す", () => __monitor.SetTargetRecursion(target, null));
                }
                Add("登録解除", () => Remove(SelectionOrRow().Where(r => r.Root).Select(r => r.Target).OfType<WatchItem>()));
            }
        }
        return menu;
    }

    private void RefreshTargets()
    {
        __targetsDirty = false;
        var rows = __state.Items.Select(item =>
        {
            int? count = item.IsFolder ? item.Inventory?.Count :
                FileExclusions.Matches(item.Path, __state.ExcludedFilePatterns) ? 0 :
                item.Baseline is null ? null : item.Baseline == ProbeState.Found ? 1 : 0;
            var state = item.Error is not null ? "監視失敗" : count is null ? "未照会" : count == 0 ? "対象なし" : "正常";
            var scope = !item.IsFolder ? "—" : item.RecursiveOverride is null ? "デフォルト" : item.Recursive ? "ON" : "OFF";
            var elapsed = item.QueryStart is not null ? "照会中" : item.QueryMs is { } ms ? $"{ms / 1000.0:0.###}秒" : "—";
            string feed = __monitor.DescribeChangeFeed(item);
            if (item.Unread > 0) state = "未読 / " + state;
            return new Row(item.Id.ToString(), item, Label(item),
                 $"{Age(item.LatestFileWriteAt)}  ｜  {scope}  ｜  {state}  ｜  {elapsed}{(item.Error is null ? "" : "：" + item.Error)}",
                item.LatestFileWriteAt, count, item.Path, Folder: item.IsFolder, Root: true,
                TargetInfo: new(state, item.LastChecked, scope, feed, elapsed, item.QueryMs));
        });
        Assign(__targets, SortTargets(rows));
    }

    private void RefreshHistory()
    {
        __historyDirty = false;
        var filter = __filter.Text?.Trim() ?? "";
        var rows = __state.History
            .Select((x, i) => new Row(i.ToString(), null, Leaf(x.Path), x.Kind, x.At, Path: x.Path))
            .Where(x => filter.Length == 0 || x.Path!.Contains(filter, StringComparison.OrdinalIgnoreCase) || x.Detail.Contains(filter, StringComparison.OrdinalIgnoreCase));
        Assign(__history, __historySort switch
        {
            1 => Sorted(rows, x => x.Detail, __historyReverse),
            2 => Sorted(rows, x => x.Path, __historyReverse),
            _ => Sorted(rows, x => x.At, __historyReverse)
        });
    }

    private void RefreshInbox()
    {
        __inboxDirty = false;
        var roots = __state.Items.Where(x => x.Unread > 0)
            .Select(x => new Row(x.Id.ToString(), x, Label(x), $"{x.Unread:N0} 件  ｜  {Age(x.LastChanged)}",
                x.LastChanged, x.Unread, x.Path, Folder: x.IsFolder, Root: true));
        var rows = new List<Row>();
        foreach (var root in Order(roots, __inboxSort, __inboxReverse))
        {
            var target = root.Target!;
            bool branches = target.IsFolder && target.PendingFiles.Count > (target.PendingFiles.ContainsKey(".") ? 1 : 0);
            bool open = __expanded.Contains(root.Key);
            rows.Add(root with { Branch = branches ? root.Key : null, Expanded = open });
            if (!branches || !open) continue;
            var children = target.PendingFiles.Values.Where(c => c.Relative != ".");
            AppendExpandedRows(rows, target, root.Key, children);
        }
        Assign(__inbox, rows);
        __emptyInbox.IsVisible = rows.Count == 0;
        RefreshDetails();
    }

    // 展開中の枝だけを深さ優先で投影する。長い階層でも呼び出しスタックを使わない。
    private void AppendExpandedRows(List<Row> rows, WatchItem target, string root, IEnumerable<FileChange> changes)
    {
        var queue = new Stack<InboxChild>();
        PushSiblings(queue, BuildChildren(target, root, "", 1, changes));
        while (queue.TryPop(out var child))
        {
            rows.Add(child.Row);
            if (child.Row.Expanded && child.Changes is { } nested)
                PushSiblings(queue, BuildChildren(target, root, child.Prefix, child.Row.Depth + 1, nested));
        }
    }

    private void PushSiblings(Stack<InboxChild> queue, List<InboxChild> children)
    {
        foreach (var child in Order(children, static c => c.Row, __inboxSort, __inboxReverse).Reverse())
            queue.Push(child);
    }

    private sealed record InboxChild(Row Row, string Prefix = "", List<FileChange>? Changes = null);

    private List<InboxChild> BuildChildren(WatchItem target, string root, string prefix, int depth, IEnumerable<FileChange> changes)
    {
        var groups = new Dictionary<string, List<FileChange>>(StringComparer.OrdinalIgnoreCase);
        var siblings = new List<InboxChild>();
        foreach (var change in changes)
        {
            var rest = change.Relative.AsSpan(prefix.Length);
            int slash = rest.IndexOfAny('\\', '/');
            if (slash < 0)
            {
                string path = Path.Combine(target.Path, change.Relative);
                var row = new Row(root + "|" + change.Relative, target, rest.ToString(), $"{change.Kind}  ｜  {Age(change.DetectedAt)}",
                    change.DetectedAt, change.Count, path, change, Depth: depth);
                siblings.Add(new(row));
                continue;
            }
            string name = rest[..slash].ToString();
            if (!groups.TryGetValue(name, out var group)) groups[name] = group = [];
            group.Add(change);
        }

        foreach (var (name, group) in groups)
        {
            var relative = prefix + name;
            var next = relative + "\\";
            var branch = root + "|" + next;
            var time = group.Max(x => x.DetectedAt);
            var row = new Row(branch, target, name, $"{group.Count:N0} ファイル  ｜  {Age(time)}", time,
                group.Count, Path.Combine(target.Path, relative), Folder: true, Depth: depth, Branch: branch,
                Expanded: __expanded.Contains(branch));
            siblings.Add(new(row, next, group));
        }
        return siblings;
    }

    private static IEnumerable<Row> Order(IEnumerable<Row> rows, int column, bool reverse) =>
        Order(rows, static row => row, column, reverse);

    private static IEnumerable<T> Order<T>(IEnumerable<T> rows, Func<T, Row> select, int column, bool reverse) => column switch
    {
        1 => Sorted(rows, x => InboxKind(select(x)), reverse),
        3 => Sorted(rows, x => select(x).At, reverse),
        2 => reverse ? rows.OrderByDescending(x => select(x).Count) : rows.OrderBy(x => select(x).Count),
        _ => reverse ? rows.OrderByDescending(x => select(x).Name, StringComparer.CurrentCultureIgnoreCase)
            : rows.OrderBy(x => select(x).Name, StringComparer.CurrentCultureIgnoreCase)
    };

    private static IEnumerable<T> Sorted<T, TKey>(IEnumerable<T> items, Func<T, TKey> key, bool descending) =>
        descending ? items.OrderByDescending(key) : items.OrderBy(key);

    private IEnumerable<Row> SortTargets(IEnumerable<Row> rows) => __targetSort switch
    {
        1 => Sorted(rows, x => x.Count, __targetReverse),
        2 => Sorted(rows, x => x.TargetInfo?.State, __targetReverse),
        3 => Sorted(rows, x => x.At, __targetReverse),
        4 => Sorted(rows, x => x.TargetInfo?.Checked, __targetReverse),
        5 => Sorted(rows, x => x.Folder, __targetReverse),
        6 => Sorted(rows, x => x.TargetInfo?.Recursive, __targetReverse),
        7 => Sorted(rows, x => x.TargetInfo?.Feed, __targetReverse),
        8 => Sorted(rows, x => x.TargetInfo?.ElapsedMs, __targetReverse),
        9 => Sorted(rows, x => x.Path, __targetReverse),
        _ => Sorted(rows, x => x.Name, __targetReverse)
    };

    private static void Assign(ListBox list, IEnumerable<Row> rows)
    {
        var next = rows.ToArray();
        if (list.ItemsSource is Row[] old && old.SequenceEqual(next)) return;
        var selected = Selected(list).Select(x => x.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        list.ItemsSource = next;
        foreach (var row in next.Where(x => selected.Contains(x.Key))) list.SelectedItems?.Add(row);
    }

    private void Toggle(string branch)
    {
        if (!__expanded.Add(branch)) __expanded.Remove(branch);
        RefreshInbox();
    }

    private void ExpandAll()
    {
        foreach (var item in __state.Items.Where(i => i.Unread > 0 && i.IsFolder))
        {
            string root = item.Id.ToString();
            __expanded.Add(root);
            foreach (var key in item.PendingFiles.Keys)
                for (int i = key.IndexOf('\\'); i >= 0; i = key.IndexOf('\\', i + 1))
                    __expanded.Add(root + "|" + key[..(i + 1)]);
        }
        RefreshInbox();
    }

    private void Sort(ListBox list, int column)
    {
        static void Set(ref int prior, ref bool reverse, int next)
        { reverse = prior == next ? !reverse : false; prior = next; }
        if (list == __targets) { Set(ref __targetSort, ref __targetReverse, column); RefreshTargets(); }
        else if (list == __history) { Set(ref __historySort, ref __historyReverse, column); RefreshHistory(); }
        else { Set(ref __inboxSort, ref __inboxReverse, column); RefreshInbox(); }
        UpdateHeadings(list);
    }
}
