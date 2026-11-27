namespace ShareFileWatch;

internal sealed class Checkpoints(ICheckpointRepository repository, ICheckpointScanner scanner, ICheckpointDownloads downloads, TimeProvider clock)
{
    internal CheckpointInfo[] List() => repository.List();

    internal CheckpointInfo Create(string name, string root, bool recursive, string exclusions, CancellationToken stop)
    {
        name = name.Trim();
        if (name.Length is 0 or > 100) throw new ArgumentException("名前は1〜100文字で指定してください。");
        if (!Path.IsPathFullyQualified(root)) throw new ArgumentException("対象フォルダは絶対パスで指定してください。");
        var scope = new CheckpointInfo(Guid.NewGuid().ToString("N"), name,
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), recursive, FileExclusions.Parse(exclusions), clock.GetUtcNow());
        var view = scanner.Scan(scope, stop);
        if (view.Errors.Count != 0)
            throw new IOException($"基準を保存できません。確認不能なフォルダが{view.Errors.Count}件あります。{view.Errors.First().Value}");
        var result = scope with { At = clock.GetUtcNow() };
        repository.Save(result, view.Files, stop);
        return result;
    }

    internal (CheckpointInfo Info, CheckpointDifference[] Changes) Compare(string id, CancellationToken stop)
    {
        var (info, baseline) = repository.Read(id, stop);
        var view = scanner.Scan(info, stop);
        var changes = new List<CheckpointDifference>();
        foreach (var (name, current) in view.Files)
        {
            stop.ThrowIfCancellationRequested();
            if (!baseline.TryGetValue(name, out var before)) changes.Add(new(name, DifferenceKind.Added, current));
            else if (before.Modified != current.Modified || before.Bytes != current.Bytes)
                changes.Add(new(name, DifferenceKind.Updated, current));
        }
        foreach (var name in baseline.Keys)
        {
            stop.ThrowIfCancellationRequested();
            if (!view.Files.ContainsKey(name) && !IsUnconfirmed(name, view.Errors))
                changes.Add(new(name, DifferenceKind.Deleted, null));
        }
        changes.AddRange(view.Errors.Select(x => new CheckpointDifference(x.Key, DifferenceKind.Unconfirmed, null, x.Value)));
        return (info, changes.OrderBy(x => x.Relative, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    internal DownloadResult[] Collect(CheckpointInfo info, IEnumerable<CheckpointDifference> changes, string output, CancellationToken stop)
    {
        output = Path.GetFullPath(output);
        if (Within(info.Root, output)) throw new IOException("対象フォルダの外に保存してください。");
        var requests = changes.Where(x => x.CanDownload).Select(x =>
        {
            var path = Path.GetFullPath(Path.Combine(info.Root, x.Relative));
            if (!Within(info.Root, path) || Within(info.Root, Path.GetFullPath(Path.Combine(output, x.Relative))))
                throw new IOException("対象フォルダの内外をまたぐ保存はできません。");
            return new DownloadRequest(path, x.Relative, x.Expected, info.Root);
        }).ToArray();
        return downloads.Save(output, requests, stop);
    }

    private static bool Within(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
            !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }

    private static bool IsUnconfirmed(string file, Dictionary<string, string> errors)
    {
        if (errors.ContainsKey("")) return true;
        for (string? folder = Path.GetDirectoryName(file); !string.IsNullOrEmpty(folder); folder = Path.GetDirectoryName(folder))
            if (errors.ContainsKey(folder)) return true;
        return false;
    }
}
