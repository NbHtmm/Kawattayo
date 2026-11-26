using System.Collections;

namespace ShareFileWatch;

internal sealed partial class MainWindow
{
    // 検知が新しいものを最初に表示。ユーザーが選んだ列は画面を更新しても維持する。
    private readonly ViewSort __inboxSort = new(3, descending: true);
    private readonly ViewSort __targetSort = new();
    private readonly ViewSort __historySort = new(0, descending: true);

    /// @brief 列を選び直したら昇順から、同じ列なら逆順に切り替える。
    private sealed class ViewSort(int column = -1, bool descending = false)
    {
        public int Column { get; private set; } = column;
        public bool Descending { get; private set; } = descending;

        public void Toggle(int nextColumn)
        {
            Descending = nextColumn == Column && !Descending;
            Column = nextColumn;
        }

        public string Heading(string title, int index) =>
            index != Column ? title : title + (Descending ? " ▼" : " ▲");

        public int Compare<T>(T left, T right, IComparer<T>? comparer = null)
        {
            comparer ??= Comparer<T>.Default;
            return Descending ? comparer.Compare(right, left) : comparer.Compare(left, right);
        }

        public int CompareText(string left, string right) =>
            Compare(left, right, StringComparer.CurrentCultureIgnoreCase);

        /// @brief 日付が未確認の行は、方向を変えても確認済み行の後ろに置く。
        public int CompareTime(DateTimeOffset? left, DateTimeOffset? right) =>
            left is null ? right is null ? 0 : 1 : right is null ? -1 : Compare(left.Value, right.Value);
    }

    private sealed class ListRowComparer(Comparison<ListViewItem> compare) : IComparer
    {
        public int Compare(object? left, object? right) =>
            left is ListViewItem x && right is ListViewItem y ? compare(x, y) : 0;
    }

    /// @brief 選択中の列と昇降順を、タイトルの矢印で示す。
    private static void UpdateListHeadings(ListView list, string[] titles, ViewSort sort)
    {
        for (int index = 0; index < titles.Length; index++)
        {
            list.Columns[index].Text = sort.Heading(titles[index], index);
        }
    }

    private static readonly string[] s_targetHeadings =
        ["名前", "状態", "最終更新", "最終照会", "種類", "完全なパス"];
    private static readonly string[] s_historyHeadings = ["検知日時", "変更", "ファイルパス"];

    private void BindTargetSorting()
    {
        __targetList.HeaderStyle = ColumnHeaderStyle.Clickable;
        __targetList.ColumnClick += (_, e) =>
        {
            __targetSort.Toggle(e.Column);
            UpdateListHeadings(__targetList, s_targetHeadings, __targetSort);
            SortTargetRows();
        };
    }

    /// @brief 設定の行を保持したまま並び替える。日時の比較には表示文字列ではなく実際の値を使う。
    private void SortTargetRows()
    {
        if (__targetSort.Column < 0 || __targetList.Items.Count < 2)
        {
            return;
        }
        var byId = __state.Items.ToDictionary(item => item.Id);
        var comparer = new ListRowComparer((left, right) => CompareTargetRows(left, right, byId));
        // 比較器を差し替えると ListView が並び替える。更新間は不要な自動挿入ソートを行わない。
        __targetList.ListViewItemSorter = comparer;
        __targetList.ListViewItemSorter = null;
    }

    private int CompareTargetRows(ListViewItem left, ListViewItem right, Dictionary<Guid, WatchItem> byId)
    {
        if (left.Tag is not Guid leftId || right.Tag is not Guid rightId ||
            !byId.TryGetValue(leftId, out var x) || !byId.TryGetValue(rightId, out var y))
        {
            return 0;
        }
        int column = __targetSort.Column;
        int result = column switch
        {
            2 => __targetSort.CompareTime(x.LatestFileWriteAt, y.LatestFileWriteAt),
            3 => __targetSort.CompareTime(x.LastChecked, y.LastChecked),
            _ => __targetSort.CompareText(left.SubItems[column].Text, right.SubItems[column].Text)
        };
        return result != 0 ? result : StringComparer.OrdinalIgnoreCase.Compare(x.Path, y.Path);
    }

    /// @brief 通知の親子関係を変えず、同じ階層にある行だけを並び替える。
    private void SortInboxSiblings(TreeNodeCollection nodes)
    {
        var ordered = nodes.Cast<TreeNode>().ToArray();
        if (ordered.Length > 1)
        {
            Array.Sort(ordered, CompareInboxNodes);
            // 既に順序どおりの場合はネイティブのツリーを作り直さない。
            if (!nodes.Cast<TreeNode>().SequenceEqual(ordered))
            {
                nodes.Clear();
                nodes.AddRange(ordered);
            }
        }
        foreach (var node in ordered)
        {
            if (node.Nodes.Count > 0)
            {
                SortInboxSiblings(node.Nodes);
            }
        }
    }

    private int CompareInboxNodes(TreeNode left, TreeNode right)
    {
        // 旧版から引き継いだ案内は、どの方向でも末尾に表示する。
        if (left.Tag is not InboxRow x || right.Tag is not InboxRow y)
        {
            return left.Tag is InboxRow ? -1 : right.Tag is InboxRow ? 1 : 0;
        }
        int result = __inboxSort.Column switch
        {
            1 => CompareInboxChange(x, y),
            2 => __inboxSort.CompareText(InboxUpdater(x), InboxUpdater(y)),
            3 => __inboxSort.CompareTime(x.DetectedAt, y.DetectedAt),
            _ => __inboxSort.CompareText(x.Label, y.Label)
        };
        if (result == 0)
        {
            result = StringComparer.CurrentCultureIgnoreCase.Compare(x.Label, y.Label);
        }
        return result != 0 ? result : StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name);
    }

    private int CompareInboxChange(InboxRow left, InboxRow right)
    {
        // 集計行どうしは「未読 10件」の文字列ではなく、実際の件数で比較する。
        if (left.UnreadCount is long x && right.UnreadCount is long y)
        {
            return __inboxSort.Compare(x, y);
        }
        // 集計行の分類名を固定して、ファイル行を挟んでも順序の一貫性を保つ。
        return __inboxSort.CompareText(left.UnreadCount is null ? left.Kind : "未読", right.UnreadCount is null ? right.Kind : "未読");
    }

    private IEnumerable<HistoryEntry> OrderedHistory()
    {
        var entries = __state.History.Reverse();
        // 同じキーの履歴は、元の新しい順を維持する。
        return __historySort.Column switch
        {
            1 => entries.Order(Comparer<HistoryEntry>.Create((x, y) => __historySort.CompareText(x.Kind, y.Kind))),
            2 => entries.Order(Comparer<HistoryEntry>.Create((x, y) => __historySort.CompareText(x.Path, y.Path))),
            _ => entries.Order(Comparer<HistoryEntry>.Create((x, y) => __historySort.Compare(x.At, y.At)))
        };
    }
}
