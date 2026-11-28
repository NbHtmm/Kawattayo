using ShareFileWatch;
using Xunit;

public sealed class DesktopWindowRowsTests
{
    private static readonly DateTimeOffset s_now = new(2026, 4, 5, 12, 30, 0, TimeSpan.FromHours(9));

    [Fact]
    public void Label_UsesTheExplicitNameAndFallsBackForBlankNames()
    {
        var item = new WatchItem { Path = @"C:\reports\summary.txt" };
        Assert.Equal("summary.txt", DesktopWindow.Label(item));
        item.DisplayName = "   ";
        Assert.Equal("summary.txt", DesktopWindow.Label(item));
        item.DisplayName = "試験結果";
        Assert.Equal("試験結果", DesktopWindow.Label(item));
    }

    [Fact]
    public void Leaf_UsesFileOrFolderNameAndPreservesDriveAndShareRoots()
    {
        Assert.Equal("trial.csv", DesktopWindow.Leaf(@"C:\jikken\trial.csv"));
        Assert.Equal("jikken", DesktopWindow.Leaf(@"C:\jikken\"));
        Assert.Equal(@"C:\", DesktopWindow.Leaf(@"C:\"));
        const string share = @"\\files\team\";
        Assert.Equal(share, DesktopWindow.Leaf(share));
        Assert.Equal("", DesktopWindow.Leaf(""));
    }

    [Fact]
    public void Age_ShowsPlaceholderForMissingTimeAndNowForFutureClockSkew()
    {
        Assert.Equal("—", DesktopWindow.Age(null, s_now));
        Assert.Equal("たった今", DesktopWindow.Age(s_now, s_now));
        Assert.Equal("たった今", DesktopWindow.Age(s_now.AddSeconds(1), s_now));
    }

    [Fact]
    public void Age_UsesInclusiveThresholdsAndTruncatesFractionalMinutesAndHours()
    {
        Assert.Equal("たった今", DesktopWindow.Age(s_now.AddMinutes(-1).AddTicks(1), s_now));
        Assert.Equal("1分前", DesktopWindow.Age(s_now.AddMinutes(-1), s_now));
        Assert.Equal("59分前", DesktopWindow.Age(s_now.AddHours(-1).AddTicks(1), s_now));
        Assert.Equal("1時間前", DesktopWindow.Age(s_now.AddHours(-1), s_now));
        Assert.Equal("23時間前", DesktopWindow.Age(s_now.AddDays(-1).AddTicks(1), s_now));
        var older = s_now.AddDays(-1);
        Assert.Equal($"{older:MM/dd HH:mm}", DesktopWindow.Age(older, s_now));
        Assert.Equal($"{older.AddDays(-2):MM/dd HH:mm}", DesktopWindow.Age(older.AddDays(-2), s_now));
    }
}
