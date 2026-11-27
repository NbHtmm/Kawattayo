namespace ShareFileWatch;

// I/O を伴わず、確定した観測値から前回との違いを計算する。
internal static class FolderComparison
{
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
