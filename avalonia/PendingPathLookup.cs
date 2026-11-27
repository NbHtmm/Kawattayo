namespace ShareFileWatch;

// 未読全件をコピーせず、該当する登録先と祖先フォルダだけを辞書で調べる。
// 同じパスが個別登録とフォルダ登録の両方に含まれていても、再通知を抑えられる。
internal sealed class PendingPathLookup
{
    private readonly Dictionary<string, WatchItem> __targets = new(StringComparer.OrdinalIgnoreCase);

    internal PendingPathLookup(IEnumerable<WatchItem> items)
    {
        foreach (var item in items) __targets.TryAdd(Path.TrimEndingDirectorySeparator(item.Path), item);
    }

    internal bool Contains(string path)
    {
        var name = Path.TrimEndingDirectorySeparator(path);
        if (__targets.TryGetValue(name, out var direct) &&
            (direct.IsFolder ? direct.PendingFiles.ContainsKey(".") : direct.Unread > 0))
            return true;

        for (var parent = Path.GetDirectoryName(name); parent is not null; parent = Path.GetDirectoryName(parent))
        {
            if (__targets.TryGetValue(Path.TrimEndingDirectorySeparator(parent), out var item) && item.IsFolder &&
                item.PendingFiles.ContainsKey(Path.GetRelativePath(item.Path, name)))
                return true;
        }
        return false;
    }
}
