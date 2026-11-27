namespace ShareFileWatch;

internal sealed partial class MainWindow
{
    private readonly Panel __viewport = new() { AutoScroll = true, Dock = DockStyle.Fill };
    // 長いファイル名に合わせてページを広げると、縮小時や横スクロール後に名前欄が画面外へ出る。
    // ページは実際に見える幅に固定し、長い名前の全体はツールチップや詳細で読めるようにする。
    private void MeasureInboxPage() => SizeInboxPage();

    private void SizeInboxPage()
    {
        if (__viewport.Controls.Count == 0) return;
        var page = __viewport.Controls[0];
        // 上辺にドックして、縦スクロールバーが出た分の幅を WinForms に引かせる。
        page.Dock = DockStyle.Top;
        int viewportHeight = __viewport.ClientSize.Height +
            (__viewport.HorizontalScroll.Visible ? SystemInformation.HorizontalScrollBarHeight : 0);
        page.Height = Math.Max(ScaleForDisplay(this, __inboxDetails?.Visible == true ? 430 : 230, scaleText: false),
            viewportHeight);
    }

    private void ClearDeletionNotices()
    {
        foreach (var item in __state.Items)
        {
            if (!item.IsFolder && item.LastChange == "消失") item.Acknowledge();
            foreach (var change in item.PendingFiles.Values.Where(c => c.Kind == "消失").ToArray())
                item.AcknowledgeFile(change);
        }
        Save(); RefreshRows();
    }

    private Action[] CaptureRows(TreeNode[] nodes) => nodes.Select(n => n.Tag).OfType<InboxRow>()
        .Select(r => GetAcknowledgement(__state.Items.Single(i => i.Id == r.Id), r)).ToArray();

    private void ApplyAcknowledgements(Action[] actions)
    {
        foreach (var action in actions) action();
        Save(); RefreshRows();
    }

    private ContextMenuStrip BuildBatchMenu(WatchItem[] items)
    {
        var menu = new ContextMenuStrip();
        var actions = items.Select(CaptureWholeTarget).ToArray();
        menu.Items.Add("コピー", null, async (_, _) => await CopyInboxTarget(items.Select(i => i.Path).ToArray()));
        menu.Items.Add("確認済みにする", null, (_, _) => ApplyAcknowledgements(actions));
        menu.Items.Add("パスをコピー", null, (_, _) => CopyText(string.Join(Environment.NewLine, items.Select(i => i.Path))));
        AddRecursiveCommands(menu, items);
        menu.Items.Add("登録解除", null, (_, _) => Unregister(items));
        return menu;
    }

    private void AddRecursiveCommands(ContextMenuStrip menu, WatchItem[] items)
    {
        var folders = items.Where(i => i.IsFolder).ToArray();
        if (folders.Length == 0) return;
        foreach (bool? enabled in new bool?[] { true, false, null })
        {
            menu.Items.Add(enabled switch { true => "再帰監視をオン", false => "再帰監視をオフ", null => "再帰監視をデフォルトに戻す" }, null, (_, _) => {
                foreach (var item in folders)
                {
                    item.RecursiveOverride = enabled;
                    item.SetRecursive(enabled ?? __allRecursive.Checked);
                }
                Save(); RefreshRows(); StartTimer(immediate: true);
            }).Enabled = folders.Any(i => i.RecursiveOverride != enabled);
        }
    }

    private sealed partial class InboxTree
    {
        private readonly HashSet<string> __chosen = new(StringComparer.OrdinalIgnoreCase);
        private string? __anchor;
        private bool __explicit;
        public bool IsChosen(TreeNode n) => __explicit ? __chosen.Contains(n.Name) : SelectedNode == n;
        public TreeNode[] Selection() => Flatten(Nodes).Where(n => n.Tag is InboxRow && IsChosen(n)).ToArray();
        public void SelectForMenu(TreeNode n)
        {
            if (!IsChosen(n)) SelectGesture(n, Keys.None);
            else SelectedNode = n;
        }
        public void SelectGesture(TreeNode n, Keys modifiers)
        {
            var prior = SelectedNode;
            if (!__explicit && prior != null) { __chosen.Clear(); __chosen.Add(prior.Name); }
            if ((modifiers & Keys.Shift) != 0)
            {
                var visible = new List<TreeNode>();
                for (var p = Nodes.Cast<TreeNode>().FirstOrDefault(); p != null; p = p.NextVisibleNode) visible.Add(p);
                int a = visible.FindIndex(p => p.Name == (__anchor ?? prior?.Name));
                int b = visible.IndexOf(n);
                if (a < 0) a = b;
                if ((modifiers & Keys.Control) == 0) __chosen.Clear();
                if (b >= 0) foreach (var p in visible.GetRange(Math.Min(a,b), Math.Abs(a-b)+1)) __chosen.Add(p.Name);
            }
            else
            {
                __anchor = n.Name;
                if ((modifiers & Keys.Control) == 0) __chosen.Clear();
                if (!__chosen.Add(n.Name)) __chosen.Remove(n.Name);
            }
            __explicit = modifiers != Keys.None;
            SelectedNode = n;
            Invalidate();
        }
        protected override void OnBeforeSelect(TreeViewCancelEventArgs e)
        {
            if (e.Action == TreeViewAction.ByKeyboard) { __explicit = false; __anchor = e.Node?.Name; }
            base.OnBeforeSelect(e);
        }
    }
}