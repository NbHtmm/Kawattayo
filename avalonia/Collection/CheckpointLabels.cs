namespace ShareFileWatch;

internal static class CheckpointLabels
{
    internal static string Describe(CheckpointInfo point) =>
        $"{point.Name} ｜ {point.At.ToLocalTime():yyyy/MM/dd HH:mm:ss} ｜ {point.Root}";

    internal static string Describe(CheckpointDifference difference) =>
        $"{(difference.Kind switch
        {
            DifferenceKind.Added => "追加", DifferenceKind.Updated => "更新",
            DifferenceKind.Deleted => "削除", _ => "確認不能"
        })} ｜ {(difference.Relative.Length == 0 ? "対象フォルダ" : difference.Relative)}" +
        (difference.Error is null ? "" : " ｜ " + difference.Error);
}
