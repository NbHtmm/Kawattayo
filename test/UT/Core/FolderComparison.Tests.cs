using ShareFileWatch;
using Xunit;

public sealed class FolderComparisonTests
{
    private static readonly DateTimeOffset s_now = new(2026, 1, 2, 0, 0, 0, TimeSpan.Zero);
    private static readonly Fingerprint s_original = new(s_now.AddHours(-2).ToFileTime(), 1, 10);

    [Fact]
    public void Compare_ReportsAddedUpdatedAndRemovedWithoutCaseOnlyChanges()
    {
        var before = Snapshot(("CHANGE.txt", s_original), ("gone.txt", s_original), ("Same.txt", s_original));
        var after = Snapshot(("change.txt", s_original with { Bytes = 11 }), ("new.txt", s_original), ("same.txt", s_original));
        var actual = FolderComparison.Compare(before, after, s_now);
        Assert.Equal(3, actual.Count);
        Assert.Contains(actual, c => c.Relative == "change.txt" && c.Kind == "更新");
        Assert.Contains(actual, c => c.Relative == "gone.txt" && c.Kind == "消失");
        Assert.Contains(actual, c => c.Relative == "new.txt" && c.Kind == "追加");
    }

    [Fact]
    public void Compare_ExclusionChangesAreNotFileChanges()
    {
        var before = Snapshot(("excluded-now.tmp", s_original));
        var after = Snapshot(("included-now.txt", s_original));
        Assert.Empty(FolderComparison.Compare(before, after, s_now, ["included-now.*"], ["*.tmp"]));
        Assert.Empty(FolderComparison.Compare(before, before, s_now));
    }

    [Fact]
    public void Summary_ShowsCountsAndLimitsPreviewToThreeShortPaths()
    {
        var summary = FolderComparison.Summary([
            new(new string('a', 121), "追加", s_now), new("b", "更新", s_now),
            new("c", "消失", s_now), new("not-in-preview", "更新", s_now)]);
        Assert.Equal("+1 / 更新2 / 消失1：" + new string('a', 117) + "…、b、c", summary);
    }

    private static Dictionary<string, Fingerprint> Snapshot(params (string Path, Fingerprint Value)[] entries) =>
        entries.ToDictionary(e => e.Path, e => e.Value, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Compare_AttachesOnlyValidChangedWriteTimesAndSilentlyRebaselinesScope()
    {
        var before = Snapshot(("timestamp.txt", s_original), ("future.txt", s_original),
            ("size.txt", s_original), ("exclude.tmp", s_original));
        var earlier = s_now.AddMinutes(-1);
        var after = Snapshot(("timestamp.txt", s_original with { Modified = earlier.ToFileTime() }),
            ("future.txt", s_original with { Modified = s_now.AddTicks(1).ToFileTime() }),
            ("size.txt", s_original with { Bytes = 11 }), ("include.tmp", s_original));
        var result = FolderComparison.Compare(before, after, s_now, ["include.*"], ["exclude.*"])
            .ToDictionary(change => change.Relative);
        Assert.Equal(3, result.Count);
        Assert.Equal(earlier, result["timestamp.txt"].ModifiedAt);
        Assert.Null(result["future.txt"].ModifiedAt);
        Assert.Null(result["size.txt"].ModifiedAt);
        Assert.All(result.Values, item => Assert.Equal(("更新", s_now), (item.Kind, item.DetectedAt)));
    }

    [Fact]
    public void Summary_EmptyAndExactPreviewLengthDoNotTruncate()
    {
        Assert.Equal("+0 / 更新0 / 消失0：", FolderComparison.Summary([]));
        string boundary = new('x', 120);
        Assert.Equal("+0 / 更新0 / 消失1：" + boundary, FolderComparison.Summary([new(boundary, "消失", s_now)]));
    }
}
