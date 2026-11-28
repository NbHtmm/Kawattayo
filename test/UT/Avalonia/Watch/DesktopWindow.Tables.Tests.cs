using System.Globalization;
using ShareFileWatch;
using Xunit;

public sealed class DesktopWindowTablesTests
{
    [Fact]
    public void TimeCell_UsesLocalTimeAndAPlaceholderForUnknownTime()
    {
        var value = new DateTimeOffset(2026, 1, 2, 20, 34, 56, TimeSpan.FromHours(-7));
        Assert.Equal("—", DesktopWindow.TimeCell(null));
        Assert.Equal(value.ToLocalTime().ToString("yyyy/MM/dd HH:mm:ss", CultureInfo.CurrentCulture),
            DesktopWindow.TimeCell(value));
    }

    [Fact]
    public void InboxKind_PrioritizesTheFileChangeThenFolderCountThenTargetThenFallback()
    {
        var file = new DesktopWindow.Row("item", new WatchItem { LastChange = "消失" }, "Name", "", null);
        Assert.Equal("消失", DesktopWindow.InboxKind(file));
        Assert.Equal("更新", DesktopWindow.InboxKind(file with { Target = null }));
        var directory = file with { Folder = true, Count = 17 };
        Assert.Equal("17 件", DesktopWindow.InboxKind(directory));
        Assert.Equal("追加", DesktopWindow.InboxKind(directory with { Change = new("file", "追加", DateTimeOffset.UnixEpoch) }));
    }
}
