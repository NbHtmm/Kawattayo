using System.ComponentModel;
using ShareFileWatch;
using Xunit;

public sealed class FolderProbeTests
{
    private static readonly string s_root = Path.GetFullPath(@"fixture\root");
    private static readonly Fingerprint s_stamp = new(1, 2, 3);

    [Fact]
    public void ReadInventory_ChangesScopeWithoutReadingChildDirectoriesInNonrecursiveMode()
    {
        var visits = new List<(string Name, int Remaining)>();
        List<FileProbe.DirectoryEntry> List(string path, int remaining, CancellationToken token)
        {
            visits.Add((path, remaining));
            return path == s_root
                ? [new("report.txt", false, s_stamp), new("cache.tmp", false, s_stamp), new("sub", true, s_stamp)]
                : [new("report.txt", false, s_stamp)];
        }

        var read = FolderProbe.ReadInventory(s_root, false, p => p.EndsWith(".tmp", StringComparison.Ordinal),
            CancellationToken.None, List);
        var files = Assert.IsType<Dictionary<string, Fingerprint>>(read.Files);
        Assert.Null(read.Error);
        Assert.Same(s_stamp, files["REPORT.TXT"]);
        Assert.Single(files);
        Assert.Equal((s_root, 50000), Assert.Single(visits));

        visits.Clear();
        var recursive = FolderProbe.ReadInventory(s_root, true, null, CancellationToken.None, List);
        var all = Assert.IsType<Dictionary<string, Fingerprint>>(recursive.Files);
        Assert.Equal(3, all.Count);
        Assert.Same(s_stamp, all[Path.Combine("sub", "report.txt")]);
        Assert.Equal((Path.Combine(s_root, "sub"), 49997), visits[1]);
    }

    [Fact]
    public void ReadInventory_EmptyFolderIsAValidCompleteObservation()
    {
        var result = FolderProbe.ReadInventory(s_root, true, null, CancellationToken.None, (_, _, _) => []);
        Assert.NotNull(result.Files);
        Assert.Empty(result.Files);
        Assert.Null(result.Error);
    }

    [Fact]
    public void ReadInventory_UnreadableChildDiscardsPartialInventoryAndPreservesDiagnostic()
    {
        foreach (var error in new Exception[]
        {
            new IOException("fixture read failure"), new UnauthorizedAccessException("fixture permission"),
            new Win32Exception(3, "fixture closed share"),
        })
        {
            var result = FolderProbe.ReadInventory(s_root, true, null, CancellationToken.None, (path, _, _) =>
                path == s_root ? [new("report.txt", false, s_stamp), new("sub", true, s_stamp)] : throw error);
            Assert.Null(result.Files);
            Assert.Equal(error.Message, result.Error);
        }
    }

    [Fact]
    public void ReadInventory_PropagatesCancellationAndUnexpectedErrors()
    {
        using var stop = new CancellationTokenSource();
        Assert.Throws<OperationCanceledException>(() =>
            FolderProbe.ReadInventory(s_root, true, null, new(true), (_, _, _) => throw new InvalidOperationException()));
        Assert.Throws<OperationCanceledException>(() =>
            FolderProbe.ReadInventory(s_root, true, null, stop.Token, (_, _, token) =>
            {
                Assert.Equal(stop.Token, token);
                stop.Cancel();
                return [new("report.txt", false, s_stamp)];
            }));
        Assert.Throws<InvalidOperationException>(() =>
            FolderProbe.ReadInventory(s_root, false, null, CancellationToken.None, (_, _, _) => throw new InvalidOperationException()));
    }

    [Fact]
    public void ReadInventory_CancellationDuringAnEmptyListingCannotPublishAnEmptySnapshot()
    {
        using var stop = new CancellationTokenSource();
        Assert.Throws<OperationCanceledException>(() =>
            FolderProbe.ReadInventory(s_root, true, null, stop.Token, (_, _, _) =>
            {
                stop.Cancel();
                return [];
            }));
    }

    [Fact]
    public void TakeInventory_ReportsEachVisitBeforeEnumeratingAndFiltersByFullPath()
    {
        var order = new List<string>();
        var result = FolderProbe.TakeInventory(s_root, true,
            path => path == Path.Combine(s_root, "sub", "ignore.txt"), CancellationToken.None,
            (path, _, _) =>
            {
                order.Add("list:" + path);
                return path == s_root
                    ? [new("sub", true, s_stamp)]
                    : [new("keep.txt", false, s_stamp), new("ignore.txt", false, s_stamp)];
            }, path => order.Add("visit:" + path));

        string child = Path.Combine(s_root, "sub");
        Assert.Equal(new[] { "visit:" + s_root, "list:" + s_root, "visit:" + child, "list:" + child }, order);
        Assert.Equal(Path.Combine("sub", "keep.txt"), Assert.Single(result).Key);
    }

}
