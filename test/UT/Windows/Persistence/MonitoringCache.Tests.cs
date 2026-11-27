using ShareFileWatch;
using Xunit;

public sealed class MonitoringCacheTests
{
    [Theory]
    [InlineData("report.txt", true)]
    [InlineData(@"run\report.txt", true)]
    [InlineData("run/report.txt", true)]
    [InlineData("run/集計 01.txt", true)]
    [InlineData("", false)]
    [InlineData(".", false)]
    [InlineData("..", false)]
    [InlineData(@"run\..\report.txt", false)]
    [InlineData(@"C:\report.txt", false)]
    [InlineData(@"C:report.txt", false)]
    [InlineData(@"\report.txt", false)]
    [InlineData(@"run\\report.txt", false)]
    [InlineData(@"run\report.txt\", false)]
    [InlineData("run/report?.txt", false)]
    public void IsRelativeEntry_AllowsOnlyUnambiguousRelativeFileNames(string path, bool expected)
    {
        Assert.Equal(expected, MonitoringCache.IsRelativeEntry(path));
    }

    [Fact]
    public void ValidateObservations_AcceptsFolderMarkerAndIndependentPendingFile()
    {
        var watch = new WatchItem { IsFolder = true, Unread = 2 };
        watch.Inventory = new() { ["report.txt"] = new(12, 5, 34) };
        watch.PendingFiles["."] = new(".", "更新", DateTimeOffset.UnixEpoch);
        watch.PendingFiles["report.txt"] = new("report.txt", "追加", DateTimeOffset.UnixEpoch);

        MonitoringCache.ValidateObservations(watch);

        Assert.Equal(2, watch.Unread);
        Assert.Equal(2, watch.PendingFiles.Count);
        Assert.Equal((ulong)34, watch.Inventory["report.txt"].Bytes);
    }

    [Fact]
    public void ValidateObservations_RejectsFolderMarkerForAFileTarget()
    {
        var watch = new WatchItem { IsFolder = false, Unread = 1 };
        watch.PendingFiles["."] = new(".", "更新", DateTimeOffset.UnixEpoch);

        Assert.Throws<IOException>(() => MonitoringCache.ValidateObservations(watch));
    }

    [Fact]
    public void ValidateObservations_RejectsEveryIndependentInvalidField()
    {
        var negativeUnread = new WatchItem { Unread = -1 };
        var invalidInventory = new WatchItem { IsFolder = true };
        invalidInventory.Inventory = new() { [""] = new(12, 5, 34) };
        var invalidPending = new WatchItem { IsFolder = true, Unread = 1 };
        invalidPending.PendingFiles[""] = new("", "更新", DateTimeOffset.UnixEpoch);

        Assert.Throws<IOException>(() => MonitoringCache.ValidateObservations(negativeUnread));
        Assert.Throws<IOException>(() => MonitoringCache.ValidateObservations(invalidInventory));
        Assert.Throws<IOException>(() => MonitoringCache.ValidateObservations(invalidPending));
    }
}
