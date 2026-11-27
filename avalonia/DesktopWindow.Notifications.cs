namespace ShareFileWatch;

internal sealed partial class DesktopWindow
{
    private void OpenNotification(string? path)
    {
        ShowFromTray();
        if (path is null) return;
        var target = __state.Items.FirstOrDefault(item => item.Unread > 0 &&
            (string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase) ||
             (item.IsFolder && item.PendingFiles.ContainsKey(Path.GetRelativePath(item.Path, path)))));
        if (target is null) return;

        var key = target.Id.ToString();
        if (target.IsFolder && !string.Equals(target.Path, path, StringComparison.OrdinalIgnoreCase))
        {
            var relative = Path.GetRelativePath(target.Path, path);
            __expanded.Add(key);
            for (var parent = Path.GetDirectoryName(relative); !string.IsNullOrEmpty(parent); parent = Path.GetDirectoryName(parent))
                __expanded.Add(key + "|" + parent + "\\");
        }
        RefreshInbox();
        var row = __inbox.Items.OfType<Row>().FirstOrDefault(x => string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase) &&
            x.Target?.Id == target.Id);
        if (row is null) return;
        __inbox.SelectedItem = row;
        __inbox.ScrollIntoView(row);
    }
}
