using System.ComponentModel;
using ShareFileWatch;
using Xunit;

public sealed class FolderProbeTests
{
    private static readonly string s_root = Path.GetFullPath(@"fixture\root");
    private static readonly Fingerprint s_stamp = new(1, 2, 3);

    private static readonly FolderProbe.ChangeSource s_closedBoundary = new(
        _ => throw new InvalidOperationException("Unexpected attribute lookup"),
        _ => throw new InvalidOperationException("Unexpected read"),
        _ => throw new InvalidOperationException("Unexpected connectivity check"),
        (_, _, _) => throw new InvalidOperationException("Unexpected enumeration"));

    [Fact]
    public void ReadChanges_EmptyQueueKeepsIndependentCaseInsensitiveBaseline()
    {
        var baseline = new Dictionary<string, Fingerprint> { ["sample.csv"] = s_stamp };
        var result = FolderProbe.ReadChanges(s_root, true, baseline, [], _ => false, CancellationToken.None, s_closedBoundary, 1);
        var copy = Assert.IsType<Dictionary<string, Fingerprint>>(result.Files);
        Assert.NotSame(baseline, copy);
        Assert.Same(s_stamp, copy["SAMPLE.CSV"]);
        Assert.Null(result.Error);
    }

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData(@"..\sibling")]
    [InlineData(@"Z:\other-drive")]
    public void ReadChanges_UnscopedEventRescansInsteadOfReadingOutsideRoot(string eventPath)
    {
        var source = s_closedBoundary with { List = (path, _, _) =>
        {
            Assert.Equal(s_root, path);
            return [new("actual.txt", false, s_stamp)];
        } };
        var result = FolderProbe.ReadChanges(s_root, false, new() { ["stale.txt"] = s_stamp }, [Path.Combine(s_root, eventPath)],
            _ => false, CancellationToken.None, source, 20);
        Assert.Equal("actual.txt", Assert.Single(result.Files!).Key);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReadChanges_ParentEventShadowsChildAndOnlyRecursiveModeEnumerates(bool recursive)
    {
        var parent = Path.Combine(s_root, "logs");
        var visited = new List<string>();
        var source = s_closedBoundary with
        {
            Attributes = path => path == parent ? FileAttributes.Directory : throw new InvalidOperationException("Covered child lookup"),
            List = (path, _, _) => path == parent ? [new("new.txt", false, s_stamp)] : throw new InvalidOperationException("Wrong folder")
        };
        var baseline = new Dictionary<string, Fingerprint> { [@"logs\old.txt"] = s_stamp, ["unrelated.txt"] = s_stamp };
        var result = FolderProbe.ReadChanges(s_root, recursive, baseline, [Path.Combine(parent, "old.txt"), parent],
            _ => false, CancellationToken.None, source, 20, visited.Add);
        Assert.Equal(2, baseline.Count);
        Assert.True(result.Files!.ContainsKey("unrelated.txt"));
        if (recursive)
        {
            Assert.Equal(parent, Assert.Single(visited));
            Assert.False(result.Files.ContainsKey(@"logs\old.txt"));
            Assert.Same(s_stamp, result.Files[@"logs\new.txt"]);
        }
        else
        {
            // Nonrecursive baselines normally contain no descendants. An event never enumerates them.
            Assert.Empty(visited);
            Assert.False(result.Files.ContainsKey(@"logs\new.txt"));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReadChanges_ChecksRootOnceForMultipleMissingPathsAndDoesNotPublishAfterDisconnect(bool available)
    {
        int checks = 0;
        var source = s_closedBoundary with
        {
            Attributes = path => Path.GetFileName(path) == "one" ? throw new FileNotFoundException() : throw new DirectoryNotFoundException(),
            RootExists = root => { Assert.Equal(s_root, root); checks++; return available; }
        };
        var baseline = new Dictionary<string, Fingerprint> { ["one"] = s_stamp, [@"two\child"] = s_stamp, [@"two-extra\keep"] = s_stamp };
        var result = FolderProbe.ReadChanges(s_root, true, baseline, [Path.Combine(s_root, "one"), Path.Combine(s_root, "two")],
            _ => false, CancellationToken.None, source, 20);
        Assert.Equal(1, checks);
        Assert.Equal(3, baseline.Count);
        if (available) Assert.Equal(@"two-extra\keep", Assert.Single(result.Files!).Key);
        else { Assert.Null(result.Files); Assert.NotNull(result.Error); }
    }

    [Fact]
    public void ReadChanges_RemovesReparseSubtreeAndReplacesFormerFolderWithFile()
    {
        string replaced = Path.Combine(s_root, "logs");
        string link = Path.Combine(s_root, "link");
        var source = s_closedBoundary with
        {
            Attributes = path => path == link ? FileAttributes.Directory | FileAttributes.ReparsePoint : FileAttributes.Normal,
            ReadFile = path => path == replaced ? new(ProbeState.Found, s_stamp) : throw new InvalidOperationException("Unexpected file")
        };
        var baseline = new Dictionary<string, Fingerprint> { [@"logs\deep\old.csv"] = s_stamp, [@"logs\tail.txt"] = s_stamp,
            [@"link\secret.txt"] = s_stamp, ["unaltered"] = s_stamp };
        var result = FolderProbe.ReadChanges(s_root, true, baseline, [replaced, link], _ => false, CancellationToken.None, source, 20);
        Assert.Equal(new[] { "logs", "unaltered" }, result.Files!.Keys.OrderBy(x => x, StringComparer.Ordinal));
        Assert.Same(s_stamp, result.Files["logs"]);
        Assert.Equal(4, baseline.Count);
    }

    [Fact]
    public void ReadChanges_ExclusionSkipsBodyReadAndMissingProbeRemovesSingleBaselineFile()
    {
        string excluded = Path.Combine(s_root, "ignored.tmp");
        string gone = Path.Combine(s_root, "gone.txt");
        var source = s_closedBoundary with
        {
            Attributes = _ => FileAttributes.Normal,
            ReadFile = path => path == gone ? new(ProbeState.Missing) : throw new InvalidOperationException("Excluded file read")
        };
        var baseline = new Dictionary<string, Fingerprint> { ["ignored.tmp"] = s_stamp, ["gone.txt"] = s_stamp };
        var result = FolderProbe.ReadChanges(s_root, false, baseline, [excluded, gone], p => p == excluded, CancellationToken.None, source, 20);
        Assert.Empty(result.Files!);
        Assert.Equal(2, baseline.Count);
    }

    [Theory]
    [InlineData("result")]
    [InlineData("io")]
    [InlineData("access")]
    [InlineData("native")]
    public void ReadChanges_FailedLaterObservationDiscardsCandidateAndRetainsOriginal(string mode)
    {
        int calls = 0;
        var source = s_closedBoundary with
        {
            Attributes = _ => FileAttributes.Normal,
            ReadFile = _ => ++calls == 1 ? new(ProbeState.Found, s_stamp) : mode switch
            {
                "result" => new(ProbeState.Unavailable, Error: "fixture failure"),
                "io" => throw new IOException("fixture failure"),
                "access" => throw new UnauthorizedAccessException("fixture failure"),
                _ => throw new Win32Exception(5, "fixture failure")
            }
        };
        var baseline = new Dictionary<string, Fingerprint>();
        var result = FolderProbe.ReadChanges(s_root, true, baseline, [Path.Combine(s_root, "a"), Path.Combine(s_root, "B")],
            _ => false, CancellationToken.None, source, 20);
        Assert.Equal(2, calls);
        Assert.Equal("fixture failure", result.Error);
        Assert.Null(result.Files);
        Assert.Empty(baseline);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, true)]
    public void ReadChanges_DeduplicatesPathsAndRejectsOnlyWhenOverLimit(int limit, bool accepted)
    {
        int reads = 0;
        var source = s_closedBoundary with { Attributes = _ => FileAttributes.Normal, ReadFile = _ => { reads++; return new(ProbeState.Found, s_stamp); } };
        var baseline = new Dictionary<string, Fingerprint> { ["keep.txt"] = s_stamp };
        var result = FolderProbe.ReadChanges(s_root, true, baseline, [Path.Combine(s_root, "add"), Path.Combine(s_root, "ADD")],
            _ => false, CancellationToken.None, source, limit);
        Assert.Equal(1, reads);
        Assert.Single(baseline);
        if (accepted) Assert.Equal(2, result.Files!.Count);
        else { Assert.Null(result.Files); Assert.NotNull(result.Error); }
    }

    [Fact]
    public void ReadChanges_PropagatesCancellationBeforeObservationAndBeforePublishing()
    {
        string file = Path.Combine(s_root, "new.txt");
        Assert.Throws<OperationCanceledException>(() =>
            FolderProbe.ReadChanges(s_root, true, [], [file], _ => false, new(true), s_closedBoundary, 20));
        using var stop = new CancellationTokenSource();
        var source = s_closedBoundary with { Attributes = _ => FileAttributes.Normal,
            ReadFile = _ => { stop.Cancel(); return new(ProbeState.Found, s_stamp); } };
        Assert.Throws<OperationCanceledException>(() =>
            FolderProbe.ReadChanges(s_root, true, [], [file], _ => false, stop.Token, source, 20));
    }

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
