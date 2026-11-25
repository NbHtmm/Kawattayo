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
internal sealed record FolderResult(Dictionary<string, Fingerprint>? Files, string? Error = null);

/// @brief フォルダのスナップショット取得と差分検出。
internal static class FolderProbe
{
    // 共有全体を誤って登録しても、メモリと巡回負荷が無制限にならないよう制限する。
    private const int EntryLimit = 50_000; //!< 一回の列挙で保持できる項目数の上限。

    /// @brief 入力から観測値または保存状態を読み込む。
    public static FolderResult Read(string root, bool recursive)
    {
        try
        {
            return new(TakeInventory(root, recursive));
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException)
        {
            // 途中までしか読めない一覧を「大量に消えた」と扱わない。
            return new(null, ex.Message);
        }
    }

    /// @brief 監視範囲を走査し比較用辞書を構築する。
    private static Dictionary<string, Fingerprint> TakeInventory(string root, bool recursive)
    {
        var files = new Dictionary<string, Fingerprint>(StringComparer.OrdinalIgnoreCase);
        // 深いフォルダでも呼び出しスタックを消費せず、親の検索を閉じてから子を開く。
        var pending = new Stack<string>();
        pending.Push("");
        int visited = 0;
        while (pending.TryPop(out var relative))
        {
            var directory = Path.Combine(root, relative);
            foreach (var entry in FileProbe.ListDirectory(directory, EntryLimit - visited))
            {
                visited++;
                var path = Path.Combine(relative, entry.Name);
                // ファイルは相対パスと指紋を今回の比較対象に加える。
                if (!entry.IsFolder)
                {
                    files[path] = entry.Fingerprint;
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

    /// @brief 前回と今回の一覧から追加・変更・消失を求める。
    public static List<FileChange> Compare(Dictionary<string, Fingerprint> before, Dictionary<string, Fingerprint> after, DateTimeOffset now)
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
                changes.Add(new(path, "追加", now));
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
            if (!after.ContainsKey(path))
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
