using ShareFileWatch;
using Xunit;

public sealed class DesktopWindowDownloadsTests
{
    private static DesktopWindow.Row Row(string? path, WatchItem? target = null, FileChange? change = null, bool folder = false) =>
        new("key", target, "name", "", null, Path: path, Change: change, Folder: folder);

    [Fact]
    public void CanDownload_RequiresAPathAndRejectsDirectoryOrDeletedChange()
    {
        var previousDeletion = new WatchItem { LastChange = "消失" };
        var validChange = new FileChange("a.txt", "更新", DateTimeOffset.UnixEpoch);
        Assert.False(DesktopWindow.CanDownload(Row(null)));
        Assert.False(DesktopWindow.CanDownload(Row(@"C:\files", folder: true)));
        Assert.False(DesktopWindow.CanDownload(Row(@"C:\files\a.txt", previousDeletion)));
        Assert.False(DesktopWindow.CanDownload(Row(@"C:\files\a.txt", change: validChange with { Kind = "消失" })));
        Assert.True(DesktopWindow.CanDownload(Row(@"C:\files\a.txt", previousDeletion, validChange)));
        Assert.True(DesktopWindow.CanDownload(Row(@"C:\files\a.txt", new WatchItem { LastChange = "更新" })));
        Assert.True(DesktopWindow.CanDownload(Row(@"C:\files\a.txt")));
    }

    [Fact]
    public void SelectDownloadRows_FiltersAndDeduplicatesInSelectionOrder()
    {
        var first = Row(@"C:\files\FIRST.txt");
        var last = Row(@"C:\files\other.exe");
        var duplicate = Row(@"c:\FILES\first.TXT") with { Name = "not the selected first row" };
        Assert.Empty(DesktopWindow.SelectDownloadRows([], false));
        Assert.Equal(new[] { first, last }, DesktopWindow.SelectDownloadRows([Row(null), first, duplicate, last], false));
    }

    [Fact]
    public void SelectDownloadRows_OpenRequiresExactlyOneSupportedDocumentAfterDeduplication()
    {
        var pdf = Row(@"C:\files\one.PDF");
        var duplicate = Row(@"c:\FILES\ONE.pdf");
        var exe = Row(@"C:\files\other.exe");
        Assert.Empty(DesktopWindow.SelectDownloadRows([], true));
        Assert.Empty(DesktopWindow.SelectDownloadRows([exe], true));
        Assert.Empty(DesktopWindow.SelectDownloadRows([pdf, exe], true));
        Assert.Same(pdf, Assert.Single(DesktopWindow.SelectDownloadRows([pdf, duplicate], true)));
    }

    [Fact]
    public void CreateDownloadRequests_ChoosesLeafForSingleFileAndKeepsSubfoldersForBulkCollection()
    {
        var target = new WatchItem { Path = @"C:\watch", IsFolder = true };
        var change = new FileChange(@"trial\same.csv", "更新", DateTimeOffset.UnixEpoch);
        var nested = Row(@"C:\watch\trial\same.csv", target, change);
        var standAlone = Row(@"D:\results\same.csv", new WatchItem { Path = @"D:\results\same.csv" });
        Assert.Empty(DesktopWindow.CreateDownloadRequests([]));
        var single = Assert.Single(DesktopWindow.CreateDownloadRequests([nested]));
        Assert.Equal(new DownloadRequest(nested.Path!, "same.csv", SourceRoot: target.Path), single);
        Assert.Equal(new[] { single with { Relative = @"trial\same.csv" }, new DownloadRequest(standAlone.Path!, "same.csv") },
            DesktopWindow.CreateDownloadRequests([nested, standAlone]));
    }
}
