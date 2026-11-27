namespace ShareFileWatch;

// 通常の照会結果だけで安定を推定する。ファイルは開かず、編集終了を保証しない。
internal sealed class SettledNotifications
{
    private sealed record Waiting(string Path, string Kind, Fingerprint? Sample, long Since);
    private readonly Dictionary<Guid, Dictionary<string, Waiting>> __targets = [];
    internal bool HasPending(Guid id) => __targets.ContainsKey(id);
    internal IEnumerable<string> Paths(Guid id) => __targets.TryGetValue(id, out var paths)
        ? paths.Values.Select(pending => pending.Path) : [];
    internal int Count => __targets.Values.Sum(paths => paths.Count);

    internal void Queue(Guid id, string path, string relative, string kind)
    {
        if (!__targets.TryGetValue(id, out var paths))
            __targets[id] = paths = new(StringComparer.OrdinalIgnoreCase);
        paths[relative] = new(path, kind, null, 0);
    }

    internal void Remove(Guid id, string relative)
    {
        if (!__targets.TryGetValue(id, out var paths)) return;
        paths.Remove(relative);
        if (paths.Count == 0) __targets.Remove(id);
    }

    internal void Retain(IReadOnlySet<Guid> retained)
    {
        foreach (var id in __targets.Keys.Where(id => !retained.Contains(id)).ToArray()) __targets.Remove(id);
    }

    internal void Invalidate()
    {
        foreach (var paths in __targets.Values)
            foreach (var (key, pending) in paths.ToArray()) paths[key] = pending with { Sample = null };
    }

    internal void Observe(Guid id, bool available, long now, long delayMs, Func<string, Fingerprint?> fingerprint,
        Func<string, bool> unread, Action<string, string> alert)
    {
        if (!__targets.TryGetValue(id, out var paths)) return;
        foreach (var (key, pending) in paths.ToArray())
        {
            if (!unread(pending.Path)) { paths.Remove(key); continue; }
            // 通信断を挟んだ空白時間は安定時間に含めない。
            if (!available) { paths[key] = pending with { Sample = null }; continue; }
            var current = fingerprint(key);
            if (current is null) { paths.Remove(key); continue; }
            if (delayMs == 0 || (current == pending.Sample && now - pending.Since >= delayMs))
            {
                alert(pending.Path, pending.Kind);
                paths.Remove(key);
            }
            else if (current != pending.Sample) paths[key] = pending with { Sample = current, Since = now };
        }
        if (paths.Count == 0) __targets.Remove(id);
    }
}
