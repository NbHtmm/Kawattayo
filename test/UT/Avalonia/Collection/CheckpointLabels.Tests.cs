using ShareFileWatch;
using Xunit;

public sealed class CheckpointLabelsTests
{
    [Theory]
    [InlineData(0, "追加")]
    [InlineData(1, "更新")]
    [InlineData(2, "削除")]
    [InlineData(3, "確認不能")]
    public void Describe_MapsEachDifferenceKindAndUsesRelativePath(int kind, string label)
    {
        Assert.Equal($"{label} ｜ sub/記録.txt",
            CheckpointLabels.Describe(new CheckpointDifference("sub/記録.txt", (DifferenceKind)kind, null)));
    }

    [Fact]
    public void Describe_EmptyPathUsesRootCaptionAndIncludesDiagnostic()
    {
        Assert.Equal("確認不能 ｜ 対象フォルダ ｜ 一時切断",
            CheckpointLabels.Describe(new CheckpointDifference("", DifferenceKind.Unconfirmed, null, "一時切断")));
    }

    [Fact]
    public void Describe_CheckpointIncludesItsNameLocalTimeAndRoot()
    {
        var date = new DateTimeOffset(2026, 5, 3, 3, 7, 11, TimeSpan.Zero);
        var point = new CheckpointInfo("id", "現場 点検", @"C:\fixture\記録", false, [], date);
        Assert.Equal($"現場 点検 ｜ {date.ToLocalTime():yyyy/MM/dd HH:mm:ss} ｜ {point.Root}",
            CheckpointLabels.Describe(point));
    }
}
