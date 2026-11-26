namespace ShareFileWatch;

internal sealed partial class MainWindow
{
    private void VerifyBatchOperations()
    {
        var now = DateTimeOffset.Now;
        var folder = new WatchItem { IsFolder = true, Path = Path.Combine(Path.GetTempPath(), "batch-folder") };
        var file = new WatchItem { Path = Path.Combine(Path.GetTempPath(), "batch-file"), Unread = 1, LastChange = "更新" };
        var original = new Fingerprint(1, 1, 0);
        folder.ObserveFolder(new(new() { ["gone"] = original }) { DirectoryStamp = 1 }, now);
        folder.ObserveFolder(new(new()) { DirectoryStamp = 2 }, now, notifyDeleted: false);
        SelfTest.Check(folder.PendingFiles.Keys.SequenceEqual(new[] { "." }) && folder.Inventory!.Count == 0, "suppressed deletion advances baseline; directory timestamp has its own notification");
        folder.ObserveFolder(new(new() { ["gone"] = original }) { DirectoryStamp = 2 }, now);
        folder.ObserveFolder(new(new()) { DirectoryStamp = 2 }, now);
        __state.Items.AddRange([folder, file]);
        try
        {
            RefreshInbox();
            SelfTest.Check(__clearDeleted.Visible, "deletion action shown for pending deletions");
            ClearDeletionNotices();
            SelfTest.Check(!__clearDeleted.Visible, "deletion action hidden after clearing");
            SelfTest.Check(folder.PendingFiles.Keys.SequenceEqual(new[] { "." }) && file.Unread == 1, "clear only deletion rows");
            SelfTest.Check(__store.Load().State.Items.Single(i => i.Id == folder.Id).PendingFiles.ContainsKey("."), "folder timestamp survives reload");
            var a = __inbox.Nodes[folder.Id.ToString("N")]!;
            var b = __inbox.Nodes[file.Id.ToString("N")]!;
            __inbox.SelectGesture(a, Keys.None);
            __inbox.SelectGesture(b, Keys.Shift);
            SelfTest.Check(__inbox.IsChosen(a) && __inbox.IsChosen(b), "shift selects both endpoints");
            __inbox.SelectGesture(a, Keys.None);
            __inbox.SelectGesture(b, Keys.Control);
            SelfTest.Check(__inbox.Selection().Length == 2, "ctrl selects two independent rows");
            MarkInboxRead();
            SelfTest.Check(folder.Unread == 0 && file.Unread == 0, "batch acknowledgement");
            using var menu = BuildBatchMenu([folder, file]);
            menu.Items.Cast<ToolStripItem>().Single(i => i.Text == "再帰監視をオン").PerformClick();
            SelfTest.Check(folder.Recursive && !file.Recursive, "recursive command affects folders only");
            __allRecursive.Checked = false;
            SelfTest.Check(folder.Recursive && __store.Load().State.Items.Single(i => i.Id == folder.Id).RecursiveOverride == true, "explicit ON survives default change and reload");
            using var reset = BuildBatchMenu([folder]);
            reset.Items.Cast<ToolStripItem>().Single(i => i.Text == "再帰監視をデフォルトに戻す").PerformClick();
            SelfTest.Check(folder.RecursiveOverride == null && !folder.Recursive, "reset follows current default");
            menu.Items.Cast<ToolStripItem>().Single(i => i.Text == "登録解除").PerformClick();
            SelfTest.Check(!__state.Items.Contains(folder) && !__state.Items.Contains(file), "batch unregister");
        }
        finally { __state.Items.Remove(folder); __state.Items.Remove(file); RefreshRows(); }
    }
}