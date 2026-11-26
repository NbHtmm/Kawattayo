namespace ShareFileWatch;

internal sealed partial class MainWindow
{
    private async Task VerifyTargetOptions(string path)
    {
        var item = __state.Items.Single(target => target.Path == path);
        item.Acknowledge();
        await File.AppendAllTextAsync(path, "first unread");
        await Scan();
        SelfTest.Check(__lastAlertCount == 1, "first unread is eligible for notification");
        __renotifyUnread.Checked = true;
        await File.AppendAllTextAsync(path, "another update");
        await Scan();
        SelfTest.Check(__lastAlertCount == 1 && __store.Load().State.RenotifyUnread && item.Unread == 2,
            "repeat notification is optional and does not add another unread row");
        __renotifyUnread.Checked = false;

        SetTargetDisplayName(item, "作業ファイル");
        var node = __inbox.Nodes.Cast<TreeNode>().Single(n => n.Tag is InboxRow row && row.Id == item.Id);
        SelfTest.Check(node.Tag is InboxRow { Label: "作業ファイル" } &&
            __targetList.Items.Cast<ListViewItem>().Single(row => Equals(row.Tag, item.Id)).Text == "作業ファイル" &&
            __store.Load().State.Items.Single(target => target.Id == item.Id).DisplayName == "作業ファイル",
            "custom name appears in both lists and reloads from cache");

        using var registeredMenu = BuildTargetMenu(item);
        SelfTest.Check(registeredMenu.Items.Cast<ToolStripItem>().Any(entry => entry.Text == "開く") &&
            registeredMenu.Items.Cast<ToolStripItem>().Any(entry => entry.Text == "表示名を変更…"), "registered targets expose open and rename");
        __verifyShellMenu = (_, menu) => menu.Items.Cast<ToolStripItem>().Single(entry => entry.Text == "確認済みにする").PerformClick();
        try { ShowInboxShellMenu(node, Point.Empty); }
        finally { __verifyShellMenu = null; }
        SelfTest.Check(item.Unread == 0 && __store.Load().State.Items.Single(target => target.Id == item.Id).Unread == 0,
            "right click acknowledges without opening the file");

        // 設定の「開く」も関連付けを起動し、成功した場合だけ既読にする。
        await File.AppendAllTextAsync(path, "open from settings");
        await Scan();
        __verifyFileOpen = candidate => candidate == path;
        try
        {
            using var openMenu = BuildTargetMenu(item);
            openMenu.Items[0].PerformClick();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (__isOpeningTarget)
            {
                await Task.Delay(10, deadline.Token);
            }
            SelfTest.Check(item.Unread == 0, "opening from registered list confirms the file");
        }
        finally { __verifyFileOpen = null; }

        SetTargetDisplayName(item, "");
        SelfTest.Check(TargetDisplayName(item) == DisplayName(path), "blank custom name restores default");
    }
}
