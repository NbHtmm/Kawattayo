namespace ShareFileWatch;

internal static class CollectorExportSelection
{
    internal static async Task<string?> ResolveDestination(CheckpointInfo requestedPreview, int selectedCount,
        Func<CheckpointInfo?> currentPreview, Func<Task<string?>> pickFolder, CancellationToken stop)
    {
        stop.ThrowIfCancellationRequested();
        if (selectedCount == 0) return null;

        string? path = await pickFolder();
        // 閉じる・中止のあとに、フォルダー選択の結果が届いてもコピーを始めない。
        stop.ThrowIfCancellationRequested();

        // 同じチェックポイントでも、再比較した結果なら以前の選択では保存しない。
        return !string.IsNullOrWhiteSpace(path) && ReferenceEquals(requestedPreview, currentPreview()) ? path : null;
    }
}
