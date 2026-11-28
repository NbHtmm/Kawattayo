using ShareFileWatch;
using Xunit;

public sealed class LocalDownloadsTests
{
    [Theory]
    [InlineData(DriveType.Fixed)]
    [InlineData(DriveType.Removable)]
    [InlineData(DriveType.Ram)]
    public void ResolveDestination_CanonicalizesAChildOnAValidLocalDrive(DriveType type)
    {
        string result = LocalDownloads.ResolveDestination(@"D:\results\",
            new(@"C:\trial\report.csv", @"logs\..\report.csv"),
            drive => { Assert.Equal(@"D:\", drive); return type; });
        Assert.Equal(@"D:\results\report.csv", result);
    }

    [Theory]
    [InlineData(DriveType.Network)]
    [InlineData(DriveType.CDRom)]
    [InlineData(DriveType.NoRootDirectory)]
    [InlineData(DriveType.Unknown)]
    public void ResolveDestination_RejectsIneligibleDriveTypes(DriveType type) =>
        Assert.Throws<IOException>(() => LocalDownloads.ResolveDestination(@"D:\results", new(@"C:\trial\a.csv", "a.csv"), _ => type));

    [Fact]
    public void ResolveDestination_RejectsNetworkDestinationsBeforeReadingDriveType() =>
        Assert.Throws<IOException>(() => LocalDownloads.ResolveDestination(@"\\nas\share\results", new(@"C:\trial\a.csv", "a.csv"),
            _ => throw new InvalidOperationException("UNC is not a local drive")));

    [Theory]
    [InlineData(".", @"C:\trial\a.csv")]
    [InlineData("..", @"C:\trial\a.csv")]
    [InlineData(@"..\results2\a.csv", @"C:\trial\a.csv")]
    [InlineData(@"C:\outside\a.csv", @"C:\trial\a.csv")]
    [InlineData("REPORT.csv", @"d:\RESULTS\report.csv")]
    public void ResolveDestination_RefusesEscapesAndCopiesOntoTheInput(string relative, string source) =>
        Assert.Throws<IOException>(() => LocalDownloads.ResolveDestination(@"D:\results", new(source, relative), _ => DriveType.Fixed));

    [Fact]
    public void EnsureSourceDirectories_ChecksTheExactParentChainAndDoesNotWalkAboveTheSelectedRoot()
    {
        var checkedPaths = new List<string>();
        LocalDownloads.EnsureSourceDirectories(@"C:\trial\logs\run\report.csv", @"c:\TRIAL",
            path => { checkedPaths.Add(path); return FileAttributes.Directory; });
        Assert.Equal(new[] { @"C:\trial\logs\run", @"C:\trial\logs", @"C:\trial" }, checkedPaths);
    }

    [Theory]
    [InlineData(@"C:\trial", @"C:\trial\logs")]
    [InlineData(@"C:\trial", @"C:\trial")]
    [InlineData(@"C:\different", null)]
    public void EnsureSourceDirectories_RefusesLinksOrSourcesOutsideTheSelectedRoot(string root, string? link)
    {
        var checkedPaths = new List<string>();
        Assert.Throws<IOException>(() => LocalDownloads.EnsureSourceDirectories(@"C:\trial\logs\report.csv", root,
            path =>
            {
                checkedPaths.Add(path);
                return FileAttributes.Directory | (path == link ? FileAttributes.ReparsePoint : 0);
            }));
        Assert.Equal(link ?? @"C:\", checkedPaths[^1]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(@"D:\")]
    [InlineData(@"D:\results")]
    [InlineData(@"D:\results\logs")]
    public void PrepareDestination_WalksRootToLeafAndStopsBeforeTraversingAnyLink(string? link)
    {
        var seen = new List<string>();
        string[] chain = [@"D:\", @"D:\results", @"D:\results\logs"];
        void Prepare() => LocalDownloads.PrepareDestination(chain[^1], path =>
        {
            seen.Add(path);
            return FileAttributes.Directory | (path == link ? FileAttributes.ReparsePoint : 0);
        });
        if (link is null) Prepare();
        else Assert.Throws<IOException>(Prepare);
        Assert.Equal(link is null ? chain : chain[..(Array.IndexOf(chain, link) + 1)], seen);
    }

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
