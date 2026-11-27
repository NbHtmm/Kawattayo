namespace ShareFileWatch;

internal sealed partial class MonitorSession
{
    // 実在確認はリモートで止まることがある。監視状態を操作しないワーカーで先に終わらせる。
    internal async Task<string[]> Register(IEnumerable<string> paths)
    {
        var requested = paths.ToArray();
        var results = await Task.Run(() => requested.Select(CheckRegistration).ToArray());
        if (__closing) return [];

        // 待機中に別の登録が入っても、反映直前の一覧に対して重複を判定する。
        var known = __state.Items.Select(x => x.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool added = false;
        foreach (var (path, folder, error) in results)
        {
            if (error is not null || !known.Add(path)) continue;
            __state.Items.Add(new() { Path = path, IsFolder = folder, Recursive = folder && __state.RecursiveFolders != false });
            added = true;
        }
        if (added) UpdateSettings(true);
        return results.Where(x => x.Error is not null).Take(5).Select(x => x.Path + "：" + x.Error).ToArray();
    }

    private (string Path, bool Folder, string? Error) CheckRegistration(string input)
    {
        try
        {
            string path = FileProbe.NormalizeFolder(input);
            if (__store.IsInternalFile(path)) return (input, false, "自身の設定・キャッシュは監視できません");
            if (Directory.Exists(path)) return (path, true, null);
            if (File.Exists(path)) return (FileProbe.Normalize(path), false, null);
            return (input, false, "ファイルやフォルダが見つかりません");
        }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        { return (input, false, error.Message); }
    }

    internal void RenameTarget(WatchItem item, string? displayName)
    {
        if (__closing || !__state.Items.Contains(item)) return;
        item.DisplayName = displayName?.Trim() ?? "";
        UpdateSettings();
    }

    internal void SetTargetRecursion(WatchItem item, bool? overrideValue)
    {
        if (__closing || !__state.Items.Contains(item) || !item.IsFolder) return;
        item.RecursiveOverride = overrideValue;
        item.SetRecursive(overrideValue ?? (__state.RecursiveFolders != false));
        UpdateSettings(true);
    }

    internal void RemoveTargets(IEnumerable<WatchItem> targets)
    {
        if (__closing) return;
        bool removed = false;
        foreach (var target in targets)
        {
            if (!__state.Items.Remove(target)) continue;
            Hooks.Disable(target.Id);
            removed = true;
        }
        if (removed) UpdateSettings(true);
    }

    internal void SetExclusions(string text)
    {
        var rules = FileExclusions.Parse(text);
        __state.ExcludedFilePatterns = rules;
        foreach (var item in __state.Items)
        {
            if (!item.IsFolder && FileExclusions.Matches(item.Path, rules)) item.Acknowledge();
            if (!item.IsFolder) continue;
            foreach (var change in item.PendingFiles.Values.Where(x => x.Relative != "." &&
                FileExclusions.Matches(x.Relative, rules)).ToArray())
                item.AcknowledgeFile(change);
        }
        UpdateSettings(true);
    }

    internal void ClearDeleted()
    {
        foreach (var item in __state.Items)
        {
            if (!item.IsFolder && item.LastChange == "消失") item.Acknowledge();
            foreach (var change in item.PendingFiles.Values.Where(x => x.Kind == "消失").ToArray()) item.AcknowledgeFile(change);
        }
        UpdateSettings();
    }
}
