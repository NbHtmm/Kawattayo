namespace ShareFileWatch;

internal sealed partial class DesktopWindow
{
    private void OpenNotification(string? path)
    {
        ShowFromTray();
        if (path is null) return;
        var target = FindNotificationTarget(__state.Items, path);
        if (target is null) return;

        foreach (var branch in NotificationBranches(target, path)) __expanded.Add(branch);
        RefreshInbox();
        var row = FindNotificationRow(__inbox.Items.OfType<Row>(), target.Id, path);
        if (row is null) return;
        __inbox.SelectedItem = row;
        __inbox.ScrollIntoView(row);
    }

    internal static WatchItem? FindNotificationTarget(IEnumerable<WatchItem> items, string path) =>
        items.FirstOrDefault(item => item.Unread > 0 &&
            (string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase) ||
             (item.IsFolder && item.PendingFiles.ContainsKey(Path.GetRelativePath(item.Path, path)))));

    // A notification for a child expands only that child's ancestors, not sibling branches.
    internal static IEnumerable<string> NotificationBranches(WatchItem target, string path)
    {
        if (!target.IsFolder || string.Equals(target.Path, path, StringComparison.OrdinalIgnoreCase)) yield break;
        var key = target.Id.ToString();
        var relative = Path.GetRelativePath(target.Path, path);
        yield return key;
        for (var parent = Path.GetDirectoryName(relative); !string.IsNullOrEmpty(parent); parent = Path.GetDirectoryName(parent))
            yield return key + "|" + parent + "\\";
    }

    internal static Row? FindNotificationRow(IEnumerable<Row> rows, Guid targetId, string path) =>
        rows.FirstOrDefault(x => string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase) && x.Target?.Id == targetId);
}
