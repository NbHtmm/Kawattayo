using System.ComponentModel;

namespace ShareFileWatch;

internal sealed record CheckpointInfo(string Id, string Name, string Root, bool Recursive, string[] Exclusions, DateTimeOffset At)
{
    public override string ToString() => $"{Name} ｜ {At.ToLocalTime():yyyy/MM/dd HH:mm:ss} ｜ {Root}";
}
internal enum DifferenceKind { Added, Updated, Deleted, Unconfirmed }
internal sealed record CheckpointDifference(string Relative, DifferenceKind Kind, Fingerprint? Expected, string? Error = null)
{
    internal bool CanDownload => Kind is DifferenceKind.Added or DifferenceKind.Updated;
    public override string ToString() => $"{(Kind switch
    {
        DifferenceKind.Added => "追加", DifferenceKind.Updated => "更新",
        DifferenceKind.Deleted => "削除", _ => "確認不能"
    })} ｜ {(Relative.Length == 0 ? "対象フォルダ" : Relative)}{(Error is null ? "" : " ｜ " + Error)}";
}

internal sealed class Checkpoints(string directory)
{
    private const int EntryLimit = 50_000;
    private readonly string database = Path.Combine(directory, "cache.sqlite3");

    internal CheckpointInfo[] List()
    {
        using var db = new InventoryDb(database, create: true);
        return db.ListCheckpoints();
    }

    internal CheckpointInfo Create(string name, string root, bool recursive, string exclusions, CancellationToken stop)
    {
        name = name.Trim();
        if (name.Length is 0 or > 100) throw new ArgumentException("名前は1〜100文字で指定してください。");
        if (!Path.IsPathFullyQualified(root)) throw new ArgumentException("対象フォルダは絶対パスで指定してください。");
        var scope = new CheckpointInfo(Guid.NewGuid().ToString("N"), name,
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), recursive, FileExclusions.Parse(exclusions), DateTimeOffset.Now);
        var view = Scan(scope, stop);
        if (view.Errors.Count != 0)
            throw new IOException($"基準を保存できません。確認不能なフォルダが{view.Errors.Count}件あります。{view.Errors.First().Value}");
        using var db = new InventoryDb(database, create: true);
        var result = scope with { At = DateTimeOffset.Now };
        db.SaveCheckpoint(result, view.Files, stop);
        return result;
    }

    internal (CheckpointInfo Info, CheckpointDifference[] Changes) Compare(string id, CancellationToken stop)
    {
        // 共有フォルダの走査中は SQLite の読み取りロックを保持しない。
        (CheckpointInfo info, Dictionary<string, Fingerprint> baseline) saved;
        using (var db = new InventoryDb(database, create: true)) saved = db.ReadCheckpoint(id, stop);
        var view = Scan(saved.info, stop);
        var changes = new List<CheckpointDifference>();
        foreach (var (name, current) in view.Files)
        {
            stop.ThrowIfCancellationRequested();
            if (!saved.baseline.TryGetValue(name, out var before)) changes.Add(new(name, DifferenceKind.Added, current));
            else if (before.Modified != current.Modified || before.Bytes != current.Bytes)
                changes.Add(new(name, DifferenceKind.Updated, current));
        }
        foreach (var name in saved.baseline.Keys)
        {
            stop.ThrowIfCancellationRequested();
            if (!view.Files.ContainsKey(name) && !IsUnconfirmed(name, view.Errors))
                changes.Add(new(name, DifferenceKind.Deleted, null));
        }
        changes.AddRange(view.Errors.Select(x => new CheckpointDifference(x.Key, DifferenceKind.Unconfirmed, null, x.Value)));
        return (saved.info, changes.OrderBy(x => x.Relative, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    internal static DownloadResult[] Collect(CheckpointInfo info, IEnumerable<CheckpointDifference> changes, string output, CancellationToken stop)
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
        return LocalDownloads.Save(output, requests, stop);
    }

    private static bool Within(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative != ".." && !relative.StartsWith("..\\", StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }

    private static bool IsUnconfirmed(string file, Dictionary<string, string> errors)
    {
        if (errors.ContainsKey("")) return true;
        for (string? folder = Path.GetDirectoryName(file); !string.IsNullOrEmpty(folder); folder = Path.GetDirectoryName(folder))
            if (errors.ContainsKey(folder)) return true;
        return false;
    }

    private static (Dictionary<string, Fingerprint> Files, Dictionary<string, string> Errors) Scan(CheckpointInfo scope, CancellationToken stop)
    {
        var files = new Dictionary<string, Fingerprint>(StringComparer.OrdinalIgnoreCase);
        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>();
        pending.Push("");
        int remaining = EntryLimit;
        while (pending.TryPop(out var relative))
        {
            stop.ThrowIfCancellationRequested();
            var folder = Path.Combine(scope.Root, relative);
            try
            {
                var attributes = File.GetAttributes(folder);
                if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory)
                    throw new IOException("通常のフォルダを指定してください。");
                foreach (var entry in FileProbe.ListDirectory(folder, remaining, stop))
                {
                    remaining--;
                    var name = Path.Combine(relative, entry.Name);
                    if (entry.IsFolder)
                    {
                        if (scope.Recursive) pending.Push(name);
                    }
                    else if (!FileExclusions.Matches(name, scope.Exclusions)) files[name] = entry.Fingerprint;
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception)
            {
                errors[relative] = error.Message;
            }
        }
        return (files, errors);
    }
}
