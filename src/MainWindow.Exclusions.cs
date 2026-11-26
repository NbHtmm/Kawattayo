namespace ShareFileWatch;

/// @brief 除外ルール変更時の比較基準・未読の調整。
internal sealed partial class MainWindow
{
    private void SetExcludedFilePatterns(string[] patterns)
    {
        var previous = __state.ExcludedFilePatterns;
        __state.ExcludedFilePatterns = patterns;
        if (FileExclusions.Same(previous, patterns))
        {
            return;
        }

        foreach (var item in __state.Items)
        {
            if (item.IsFolder)
            {
                item.Revision++; // 旧ルールで実行中の一覧は反映しない。
                // 旧基準の一覧は残す。次回は両方のルールで対象になっていたファイル同士を比較する。
                foreach (var relative in item.PendingFiles.Keys.Where(x => FileExclusions.Matches(x, patterns)).ToArray())
                {
                    item.Unread = Math.Max(0, item.Unread - item.PendingFiles[relative].Count);
                    item.PendingFiles.Remove(relative);
                }
            }
            else if (FileExclusions.Matches(item.Path, previous) != FileExclusions.Matches(item.Path, patterns))
            {
                item.Revision++;
                item.Error = null;
                item.LastChecked = null;
                item.Baseline = null; // 除外解除後の初回を出現・消失として通知しない。
                item.Fingerprint = null;
                item.LatestFileWriteAt = null;
                item.Unread = 0;
            }
        }
    }
}
