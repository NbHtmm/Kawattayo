namespace ShareFileWatch;

internal sealed partial class MainWindow
{
    /// @brief 実際の見出しクリックを送り、監視対象の選択と昇降順の切り替えを確認する。
    private void VerifyTargetColumnSorting()
    {
        var selected = __targetList.SelectedItems.Cast<ListViewItem>().Single();
        var ascending = __targetList.Items.Cast<ListViewItem>().Select(row => row.Text)
            .Order(StringComparer.CurrentCultureIgnoreCase).ToArray();
        ClickNativeHeader(__targetList, 0);
        SelfTest.Check(__targetList.Columns[0].Text.EndsWith("▲") &&
            __targetList.Items.Cast<ListViewItem>().Select(row => row.Text).SequenceEqual(ascending),
            "target name column sorts ascending");
        ClickNativeHeader(__targetList, 0);
        RefreshManagementRows();
        SelfTest.Check(__targetList.Columns[0].Text.EndsWith("▼") &&
            __targetList.Items.Cast<ListViewItem>().Select(row => row.Text).SequenceEqual(ascending.Reverse()) &&
            ReferenceEquals(__targetList.SelectedItems.Cast<ListViewItem>().Single(), selected),
            "target name column toggles descending and preserves selection on refresh");
    }

    /// @brief 未読ツリーは階層や選択を維持して、兄弟行だけを並び替える。
    private void VerifyInboxColumnSorting()
    {
        var branch = __inbox.Nodes[0].Nodes[0];
        branch.Parent!.Expand();
        branch.Expand();
        var oldSelection = branch.Nodes.Cast<TreeNode>().Single(node => node.Tag is InboxRow { Label: "stable.txt" });
        __inbox.SelectedNode = oldSelection;
        var header = (InboxColumnHeader)__inboxColumns;
        var click = new MouseEventArgs(MouseButtons.Left, 1, 12, 9, 0);
        header.HandleSortClick(click);
        SelfTest.Check(__inbox.Nodes[0].Nodes[0].Nodes[0].Tag is InboxRow { Label: "changed.txt" },
            "inbox name column sorts sibling files ascending");
        header.HandleSortClick(click);
        var currentBranch = __inbox.Nodes[0].Nodes[0];
        SelfTest.Check(currentBranch.IsExpanded && currentBranch.Parent!.IsExpanded &&
            currentBranch.Nodes[0].Tag is InboxRow { Label: "stable.txt" } &&
            __inbox.SelectedNode?.Name == oldSelection.Name,
            "inbox name column toggles descending without flattening or losing selection");

        var two = new InboxRow(Guid.NewGuid(), "two", null, null, false, null, null, "未読 2件", true, UnreadCount: 2);
        var ten = two with { Label = "ten", Kind = "未読 10件", UnreadCount = 10 };
        __inboxSort.Toggle(1);
        SelfTest.Check(CompareInboxChange(two, ten) < 0, "inbox change column compares group counts numerically");
        __inboxSort.Toggle(1);
        SelfTest.Check(CompareInboxChange(two, ten) > 0, "inbox change column reverses group count order");
        __inboxSort.Toggle(3);
        __inboxSort.Toggle(3);
        RefreshInbox();
    }

    /// @brief 履歴の日時列は表示文字列ではなく実際の時刻を昇降順に比較する。
    private void VerifyHistoryColumnSorting()
    {
        var list = __historyRows!;
        var before = list.Items.Cast<ListViewItem>().Select(row => ((HistoryEntry)row.Tag!).At).ToArray();
        ClickNativeHeader(list, 0);
        SelfTest.Check(list.Columns[0].Text.EndsWith("▲") &&
            list.Items.Cast<ListViewItem>().Select(row => ((HistoryEntry)row.Tag!).At).SequenceEqual(before.Order()),
            "history date column toggles ascending");
        ClickNativeHeader(list, 0);
        SelfTest.Check(list.Columns[0].Text.EndsWith("▼") &&
            list.Items.Cast<ListViewItem>().Select(row => ((HistoryEntry)row.Tag!).At).SequenceEqual(before.OrderDescending()),
            "history date column toggles descending");
    }

    private static void ClickNativeHeader(ListView list, int column)
    {
        const uint GetListHeader = 0x101f;
        nint header = SendMessage(list.Handle, GetListHeader, 0, 0);
        int x = list.Columns.Cast<ColumnHeader>().Take(column).Sum(heading => heading.Width) + 12;
        ClickHeader(header, x);
    }

    private static void ClickHeader(nint header, int x)
    {
        const uint LeftButtonDown = 0x201;
        const uint LeftButtonUp = 0x202;
        int point = x | (9 << 16);
        SendMessage(header, LeftButtonDown, 1, point);
        SendMessage(header, LeftButtonUp, 0, point);
    }
}
