using System.ComponentModel;

namespace ShareFileWatch;

/// @brief 相対パスごとの最新変更と未確認回数。
internal sealed record FileChange(string Relative, string Kind, DateTimeOffset DetectedAt, DateTimeOffset? ModifiedAt = null, int Count = 1)
{
    /// @brief 表示可能な最終更新日時へ変換する。
    public static DateTimeOffset? GetModifiedTime(long raw, DateTimeOffset detectedAt)
    {
        // 有効な更新時刻がないときは、日時を表示しない。
        if (raw <= 0)
        {
            return null;
        }

        try
        {
            var value = DateTimeOffset.FromFileTime(raw);
            return value <= detectedAt ? value : null;
        }
        catch (ArgumentOutOfRangeException) { return null; }
    }
}

/// @brief フォルダ内の比較用一覧または取得失敗理由。
internal sealed record FolderResult(Dictionary<string, Fingerprint>? Files, string? Error = null)
{
    public long? DirectoryStamp { get; init; }
    public string[] Exclusions { get; init; } = []; //!< この一覧を取得した時点のファイル名除外ルール。
}

/// @brief フォルダのスナップショット取得と差分検出。
internal static class FolderProbe
{
    // 共有全体を誤って登録しても、メモリと巡回負荷が無制限にならないよう制限する。
    private const int EntryLimit = 50_000; //!< 一回の列挙で保持できる項目数の上限。

    /// @brief 入力から観測値または保存状態を読み込む。
    public static FolderResult Read(string root, bool recursive, Func<string, bool>? exclude = null, CancellationToken cancellation = default)
    {
        try
        {
            return new(TakeInventory(root, recursive, exclude, cancellation));
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException)
        {
            // 途中までしか読めない一覧を「大量に消えた」と扱わない。
            return new(null, ex.Message);
        }
    }

    /// @brief 監視範囲を走査し比較用辞書を構築する。
    private static Dictionary<string, Fingerprint> TakeInventory(string root, bool recursive, Func<string, bool>? exclude, CancellationToken cancellation, Action<string>? onEnumerate = null)
    {
        var files = new Dictionary<string, Fingerprint>(StringComparer.OrdinalIgnoreCase);
        // 深いフォルダでも呼び出しスタックを消費せず、親の検索を閉じてから子を開く。
        var pending = new Stack<string>();
        pending.Push("");
        int visited = 0;
        while (pending.TryPop(out var relative))
        {
            var directory = Path.Combine(root, relative);
            cancellation.ThrowIfCancellationRequested();
            onEnumerate?.Invoke(directory);
            foreach (var entry in FileProbe.ListDirectory(directory, EntryLimit - visited, cancellation))
            {
                visited++;
                cancellation.ThrowIfCancellationRequested();
                var path = Path.Combine(relative, entry.Name);
                // ファイルは相対パスと指紋を今回の比較対象に加える。
                if (!entry.IsFolder)
                {
                    if (exclude?.Invoke(Path.Combine(root, path)) != true)
                    {
                        files[path] = entry.Fingerprint;
                    }
                }
                // フォルダは再帰監視のときだけ、次の探索対象に加える。
                else if (recursive)
                {
                    pending.Push(path);
                }
            }
        }
        return files;
    }

    // Assemble a candidate locally. Never modify the last complete snapshot on a failed or cancelled read.
    internal static FolderResult ReadChanges(string root, bool recursive, Dictionary<string,Fingerprint> baseline,
        string[] paths, Func<string,bool> exclude, CancellationToken cancellation, Action<string>? onEnumerate = null)
    {
        var files = new Dictionary<string,Fingerprint>(baseline, StringComparer.OrdinalIgnoreCase);
        HashSet<string>? formerFolders = null;
        bool sawMissing = false;
        try
        {
            var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p.Length))
            {
                cancellation.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(root,path);
                if (relative == "." || relative == ".." || relative.StartsWith(@"..\") || Path.IsPathRooted(relative))
                    return Read(root,recursive,exclude,cancellation);
                if (CoveredByParent(relative)) continue;
                bool CoveredByParent(string name)
                {
                    for (var p = Path.GetDirectoryName(name); !string.IsNullOrEmpty(p); p = Path.GetDirectoryName(p))
                        if (covered.Contains(p)) return true;
                    return false;
                }
                FileAttributes attributes;
                try { attributes = File.GetAttributes(path); }
                catch (IOException ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
                {
                    sawMissing = true;
                    RemoveTree(relative);
                    covered.Add(relative);
                    continue;
                }
                if (attributes.HasFlag(FileAttributes.ReparsePoint)) { RemoveTree(relative); covered.Add(relative); continue; }
                if (attributes.HasFlag(FileAttributes.Directory))
                {
                    RemoveTree(relative);
                    if (recursive)
                        foreach (var (child,value) in TakeInventory(path,true,exclude,cancellation,onEnumerate))
                            files[Path.Combine(relative,child)] = value;
                    covered.Add(relative);
                }
                else
                {
                    // A single coalesced name can represent directory deletion followed by file creation.
                    if (recursive && !files.ContainsKey(relative) && WasFolder(relative)) RemoveTree(relative);
                    covered.Add(relative);
                    if (exclude(path)) { files.Remove(relative); continue; }
                    var result = FileProbe.Read(path);
                    if (result.State == ProbeState.Unavailable) return new(null,result.Error);
                    if (result.Fingerprint is {} fp) files[relative] = fp; else RemoveTree(relative);
                }
                if (files.Count > EntryLimit) return new(null,"監視範囲の上限を超えました。");
            }
            // 消失ごとに共有ルートへ再照会しない。一覧を公開する前に一度だけ通信断を確認する。
            cancellation.ThrowIfCancellationRequested();
            if (sawMissing && !Directory.Exists(root)) return new(null, "登録フォルダに接続できません");
            return new(files);
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException) { return new(null,ex.Message); }

        bool WasFolder(string relative)
        {
            if (formerFolders == null)
            {
                formerFolders = new(StringComparer.OrdinalIgnoreCase);
                foreach (var name in baseline.Keys)
                {
                    cancellation.ThrowIfCancellationRequested();
                    for (var p = Path.GetDirectoryName(name); !string.IsNullOrEmpty(p) && formerFolders.Add(p); p = Path.GetDirectoryName(p)) { }
                }
            }
            return formerFolders.Contains(relative);
        }

        void RemoveTree(string relative)
        {
            // 前回が通常ファイルなら子孫は存在しない。大量削除で毎回全件走査しない。
            if (files.Remove(relative) || !recursive || !WasFolder(relative)) return;
            string prefix = relative + Path.DirectorySeparatorChar;
            foreach (var key in files.Keys.Where(k => k.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)).ToArray()) files.Remove(key);
        }
    }

    /// @brief 前回と今回の一覧から追加・変更・消失を求める。
    public static List<FileChange> Compare(Dictionary<string, Fingerprint> before, Dictionary<string, Fingerprint> after,
        DateTimeOffset now, string[]? previouslyExcluded = null, string[]? nowExcluded = null)
    {
        // 両方ともパスの大文字小文字を区別しない辞書。前回一覧のコピーは不要。
        // 同じスナップショットを比較する場合は差分がない。
        if (ReferenceEquals(before, after))
        {
            return [];
        }

        var changes = new List<FileChange>();
        foreach (var (path, current) in after)
        {
            // 前回の一覧になかったパスは、新規追加として記録する。
            if (!before.TryGetValue(path, out var prior))
            {
                // 除外解除で初めて見えるファイルは、追加ではなく新しい基準にする。
                if (!FileExclusions.Matches(path, previouslyExcluded))
                {
                    changes.Add(new(path, "追加", now));
                }
            }
            // 既存パスの指紋が変わった場合は、追加ではなく更新として記録する。
            else if (prior != current)
            {
                var modified = current.Modified != prior.Modified ? FileChange.GetModifiedTime(current.Modified, now) : null;
                changes.Add(new(path, "更新", now, modified));
            }
        }
        foreach (var path in before.Keys)
        {
            // 前回だけ存在したパスは消失として記録する。
            // 新たに除外した名前は、実際に削除されたと区別する。
            if (!after.ContainsKey(path) && !FileExclusions.Matches(path, nowExcluded))
            {
                changes.Add(new(path, "消失", now));
            }
        }

        return changes;
    }

    /// @brief 通知用に差分の概要をまとめる。
    public static string Summary(List<FileChange> changes)
    {
        int added = changes.Count(x => x.Kind == "追加");
        int modified = changes.Count(x => x.Kind == "更新");
        int removed = changes.Count - added - modified;
        return $"+{added} / 更新{modified} / 消失{removed}：" +
            string.Join("、", changes.Take(3).Select(x => x.Relative.Length > 120 ? x.Relative[..117] + "…" : x.Relative));
    }
}
