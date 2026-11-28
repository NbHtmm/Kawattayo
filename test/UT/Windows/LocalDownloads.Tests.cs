using ShareFileWatch;
using Xunit;

public sealed class LocalDownloadsTests
{
    [Theory]
    [InlineData("report.TXT", true)]
    [InlineData("sub/report.PdF", true)]
    [InlineData("report.xlsx", true)]
    [InlineData("document.log", true)]
    [InlineData("document.csv", true)]
    [InlineData("document.tsv", true)]
    [InlineData("document.png", true)]
    [InlineData("document.jpg", true)]
    [InlineData("document.jpeg", true)]
    [InlineData("document.gif", true)]
    [InlineData("document.webp", true)]
    [InlineData("document.bmp", true)]
    [InlineData("document.docx", true)]
    [InlineData("document.pptx", true)]
    [InlineData("document.odt", true)]
    [InlineData("document.ods", true)]
    [InlineData("document.odp", true)]
    [InlineData("report.txt.exe", false)]
    [InlineData("report.lnk", false)]
    [InlineData("README", false)]
    [InlineData("report.", false)]
    [InlineData("report.doc", false)]
    [InlineData("report.zip", false)]
    [InlineData("report.cmd", false)]
    [InlineData("report.txt.pdf.zip", false)]
    [InlineData("", false)]
    public void CanOpen_UsesFinalExtensionAndOnlyAllowsSupportedDocuments(string path, bool expected) =>
        Assert.Equal(expected, LocalDownloads.CanOpen(path));

    [Fact]
    public void SaveBatch_EmptyOrPreviouslyCanceledNeverInvokesPublisher()
    {
        var requested = new[] { new DownloadRequest("first", "first.txt"), new DownloadRequest("second", "second.txt") };
        var stopped = new CancellationToken(true);
        static DownloadResult Unexpected(string folder, DownloadRequest request, CancellationToken stop) =>
            throw new InvalidOperationException("Unexpected publisher call: " + request.Source);
        Assert.Empty(LocalDownloads.SaveBatch("output", [], CancellationToken.None, Unexpected));
        Assert.Empty(LocalDownloads.SaveBatch("output", [], stopped, Unexpected));
        var actual = LocalDownloads.SaveBatch("output", requested, stopped, Unexpected);
        Assert.Equal(new[] { new DownloadResult("first", null, "キャンセル"), new DownloadResult("second", null, "キャンセル") }, actual);
        Assert.Equal(actual, new LocalDownloads().Save("output", requested, stopped));
    }

    [Fact]
    public void SaveBatch_CancelAfterFirstCompletedItemRetainsItAndStopsLaterWrites()
    {
        using var stop = new CancellationTokenSource();
        var requested = new[] { new DownloadRequest("first", "first.txt"), new DownloadRequest("second", "second.txt") };
        var published = new List<string>();
        var actual = LocalDownloads.SaveBatch("output", requested, stop.Token, (folder, item, token) =>
        {
            Assert.Equal("output", folder);
            Assert.Equal(stop.Token, token);
            published.Add(item.Source);
            stop.Cancel();
            return new(item.Source, Path.Combine(folder, item.Relative), null);
        });
        Assert.Equal("first", Assert.Single(published));
        Assert.Equal(new[] { new DownloadResult("first", Path.Combine("output", "first.txt"), null),
            new DownloadResult("second", null, "キャンセル") }, actual);
    }

    [Fact]
    public void SaveBatch_PreservesPerItemFailureAndContinuesWhenNotCanceled()
    {
        var requested = new[] { new DownloadRequest("first", "first.txt"), new DownloadRequest("second", "second.txt") };
        var published = new List<string>();
        var actual = LocalDownloads.SaveBatch("output", requested, CancellationToken.None, (folder, item, _) =>
        {
            published.Add(item.Source);
            return item.Source == "first" ? new(item.Source, null, "occupied") :
                new(item.Source, Path.Combine(folder, item.Relative), null);
        });
        Assert.Equal(new[] { "first", "second" }, published);
        Assert.Equal(new[] { new DownloadResult("first", null, "occupied"),
            new DownloadResult("second", Path.Combine("output", "second.txt"), null) }, actual);
    }
}
