using ShareFileWatch;
using Xunit;

public sealed class MonitorSessionTests
{
    [Fact]
    public void DescribeStatus_PreservesSaveAndPollErrorsAboveNormalActivity()
    {
        var at = new DateTimeOffset(2026, 1, 5, 8, 9, 7, TimeSpan.FromHours(9));
        Assert.Equal("保存に失敗：書込不可", MonitorSession.DescribeStatus("書込不可", "照会失敗", true, 2, 10, at));
        Assert.Equal("照会失敗", MonitorSession.DescribeStatus(null, "照会失敗", true, 2, 10, at));
        Assert.Equal("一時停止中", MonitorSession.DescribeStatus(null, null, true, 2, 10, at));
    }

    [Fact]
    public void DescribeStatus_DistinguishesActiveEmptyWaitingAndCompletedMonitoring()
    {
        var at = new DateTimeOffset(2026, 1, 5, 8, 9, 7, TimeSpan.FromHours(9));
        Assert.Equal("照会中 2系統", MonitorSession.DescribeStatus(null, null, false, 2, 10, at));
        Assert.Equal("監視対象を追加してください", MonitorSession.DescribeStatus(null, null, false, 0, 0, at));
        Assert.Equal("監視中", MonitorSession.DescribeStatus(null, null, false, 0, 3, null));
        Assert.Equal("最終巡回 08:09:07", MonitorSession.DescribeStatus(null, null, false, 0, 3, at));
    }
}
