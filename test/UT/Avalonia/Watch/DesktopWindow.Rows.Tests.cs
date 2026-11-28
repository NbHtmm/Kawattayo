using ShareFileWatch;
using Xunit;

public sealed class DesktopWindowRowsTests
{
    private static readonly DateTimeOffset s_now = new(2026, 4, 5, 12, 30, 0, TimeSpan.FromHours(9));

    [Theory]
    [InlineData(0, "A", "B")]
    [InlineData(1, "B", "A")]
    [InlineData(2, "B", "A")]
    [InlineData(3, "B", "A")]
    [InlineData(4, "A", "B")]
    [InlineData(5, "A", "B")]
    [InlineData(6, "B", "A")]
    [InlineData(7, "A", "B")]
    [InlineData(8, "B", "A")]
    [InlineData(9, "A", "B")]
    [InlineData(-1, "A", "B")]
    public void SortTargets_UsesTheChosenColumnInBothDirections(int column, string first, string last)
    {
        // Opposite order across fields exposes accidentally sorting by the wrong column.
        DesktopWindow.Row a = new("A", null, "Alpha", "", s_now, 10, "A-path",
            TargetInfo: new("Zulu", s_now.AddMinutes(-1), "ON", "A", "9", 9000));
        DesktopWindow.Row b = new("B", null, "Beta", "", s_now.AddMinutes(-1), 2, "Z-path", Folder: true,
            TargetInfo: new("Alpha", s_now, "OFF", "B", "2", 2000));
        Assert.Equal(new[] { first, last }, DesktopWindow.SortTargets([b, a], column, false).Select(x => x.Key));
        Assert.Equal(new[] { last, first }, DesktopWindow.SortTargets([a, b], column, true).Select(x => x.Key));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    public void SortTargets_MissingValuesSortBeforeValuesAndPreserveTies(int column)
    {
        var empty = new DesktopWindow.Row("empty", null, "Empty", "", null);
        var known = new DesktopWindow.Row("known", null, "Known", "", s_now, 3, "path",
            TargetInfo: new("ready", s_now, "ON", "poll", "1", 1000));
        var other = empty with { Key = "other" };
        Assert.Equal(new[] { "empty", "other", "known" }, DesktopWindow.SortTargets([known, empty, other], column, false).Select(x => x.Key));
        Assert.Equal(new[] { "known", "other", "empty" }, DesktopWindow.SortTargets([other, empty, known], column, true).Select(x => x.Key));
    }

    [Theory]
    [InlineData(0, "A", "B")]
    [InlineData(1, "B", "A")]
    [InlineData(2, "B", "A")]
    [InlineData(3, "B", "A")]
    [InlineData(99, "A", "B")]
    public void Order_UsesColumnAndDirectionForBothRowsAndTheirProjections(int column, string first, string last)
    {
        DesktopWindow.Row a = new("A", new WatchItem { LastChange = "Zulu" }, "alpha", "", s_now, 20);
        DesktopWindow.Row b = new("B", new WatchItem { LastChange = "Alpha" }, "BETA", "", null);
        Assert.Equal(new[] { first, last }, DesktopWindow.Order([b, a], column, reverse: false).Select(x => x.Key));
        Assert.Equal(new[] { last, first }, DesktopWindow.Order([a, b], column, reverse: true).Select(x => x.Key));
        (DesktopWindow.Row Row, int Order)[] projected = [(b, 2), (a, 1)];
        Assert.Equal(new[] { first, last }, DesktopWindow.Order(projected, x => x.Row, column, false).Select(x => x.Row.Key));
        Assert.Equal(new[] { last, first }, DesktopWindow.Order(projected, x => x.Row, column, true).Select(x => x.Row.Key));
    }

    [Fact]
    public void Order_NameTiesIgnoreCaseAndRetainArrivalOrder()
    {
        var a = new DesktopWindow.Row("one", null, "same", "", null);
        var b = a with { Key = "two", Name = "SAME" };
        Assert.Equal(new[] { "two", "one" }, DesktopWindow.Order([b, a], 0, false).Select(x => x.Key));
        Assert.Equal(new[] { "one", "two" }, DesktopWindow.Order([a, b], 0, true).Select(x => x.Key));
    }

    [Fact]
    public void Sorted_LeavesEqualKeysStableInEitherDirectionAndDoesNotInventRows()
    {
        (int Key, string Id)[] values = [(2, "a"), (1, "b"), (2, "c"), (1, "d")];
        Assert.Equal(new[] { "b", "d", "a", "c" }, DesktopWindow.Sorted(values, x => x.Key, false).Select(x => x.Id));
        Assert.Equal(new[] { "a", "c", "b", "d" }, DesktopWindow.Sorted(values, x => x.Key, true).Select(x => x.Id));
        Assert.Empty(DesktopWindow.Sorted(Array.Empty<int>(), x => x, false));
    }

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
