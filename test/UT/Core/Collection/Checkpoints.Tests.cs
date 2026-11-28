using ShareFileWatch;
using Xunit;

public sealed class CheckpointsTests
{
    [Fact]
    public void CompareAndCollect_PreserveScopeAndIgnoreUnreadableSubtrees()
    {
        var token = TestContext.Current.CancellationToken;
        var repository = new MemoryRepository();
        var scanner = new ScriptedScanner();
        var downloads = new CopySpy();
        var service = new Checkpoints(repository, scanner, downloads, TimeProvider.System);
        string source = Path.GetFullPath("source");
        string destination = Path.GetFullPath("export");
        string hidden = Path.Combine("locked", "evidence.log");
        scanner.View.Files["deleted.txt"] = new(100, 10, 400);
        scanner.View.Files["modified.txt"] = new(100, 20, 500);
        scanner.View.Files["created-stamp-only.txt"] = new(100, 20, 500);
        scanner.View.Files[hidden] = new(200, 10, 300);
        var point = service.Create("試験前", source, true, "~$*", token);

        scanner.View = new(new(StringComparer.OrdinalIgnoreCase)
        {
            ["added.txt"] = new(200, 10, 300), ["modified.txt"] = new(300, 20, 700),
            ["created-stamp-only.txt"] = new(100, 50, 500)
        }, new(StringComparer.OrdinalIgnoreCase) { ["locked"] = "アクセス拒否" });
        var (_, differences) = service.Compare(point.Id, token);
        var types = differences.ToDictionary(x => x.Relative, x => x.Kind);
        Require(types.Count == 4 && types["locked"] == DifferenceKind.Unconfirmed &&
            types["deleted.txt"] == DifferenceKind.Deleted && types["modified.txt"] == DifferenceKind.Updated &&
            types["added.txt"] == DifferenceKind.Added && !types.ContainsKey(hidden), "部分的な照会失敗と、同じサイズ・更新時刻の判定");
        Require(repository.List().Single().Exclusions.SequenceEqual(new[] { "~$*" }), "取得時の除外を維持");

        service.Collect(point, differences, destination, token);
        Require(downloads.Paths.SequenceEqual(new[] { "added.txt", "modified.txt" }), "保存できる差分だけを渡す");
        Reject(() => service.Collect(point, differences, source, token), "監視元には出力しない");
        Reject(() => service.Collect(point, [new(Path.Combine("..", "outside.txt"), DifferenceKind.Added, new(0, 0, 1))],
            destination, token), "相対パスが外へ出ない");
        Reject(() => service.Create("失敗", source, true, "", token), "不完全な一覧で基準を確定しない");
        Require(repository.List().Length == 1, "既存の基準を変更しない");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_RejectsEmptyNamesAndDoesNotPersist(string name)
    {
        var repository = new MemoryRepository();
        var service = new Checkpoints(repository, new ScriptedScanner(), new CopySpy(), TimeProvider.System);
        Assert.Throws<ArgumentException>(() => service.Create(name, Path.GetFullPath("source"), false, "", TestContext.Current.CancellationToken));
        Assert.Empty(repository.List());
    }

    [Fact]
    public void Create_StoresTrimmedNameAndFrozenScanPolicy()
    {
        var repository = new MemoryRepository();
        var clock = new FixedClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var scanner = new ScriptedScanner();
        var service = new Checkpoints(repository, scanner, new CopySpy(), clock);
        var source = Path.GetFullPath("source") + Path.DirectorySeparatorChar;
        var point = service.Create("  基準  ", source, false, "~$*\n*.tmp\n*.TMP", TestContext.Current.CancellationToken);
        Assert.Equal("基準", point.Name);
        Assert.Equal(Path.TrimEndingDirectorySeparator(source), point.Root);
        Assert.False(point.Recursive);
        Assert.Equal(new[] { "~$*", "*.tmp" }, point.Exclusions);
        Assert.Equal(clock.GetUtcNow(), point.At);
        Assert.Equal(point, Assert.Single(service.List()));
        Assert.Empty(service.Compare(point.Id, TestContext.Current.CancellationToken).Changes);
    }

    [Fact]
    public void Compare_RejectsCancellationBeforeReturningChanges()
    {
        var repository = new MemoryRepository();
        var scanner = new ScriptedScanner();
        var service = new Checkpoints(repository, scanner, new CopySpy(), TimeProvider.System);
        var point = service.Create("baseline", Path.GetFullPath("source"), true, "", TestContext.Current.CancellationToken);
        scanner.View.Files["added.txt"] = new(1, 1, 1);
        var stop = new CancellationToken(canceled: true);
        Assert.Throws<OperationCanceledException>(() => service.Compare(point.Id, stop));
    }


    [Fact]
    public void Compare_RootFailureReportsUnconfirmedAndDoesNotReplaceTheBaseline()
    {
        var repository = new MemoryRepository();
        var scanner = new ScriptedScanner();
        var service = new Checkpoints(repository, scanner, new CopySpy(), TimeProvider.System);
        var stop = TestContext.Current.CancellationToken;
        var original = new Fingerprint(1, 1, 9);
        scanner.View.Files["keep.txt"] = original;
        scanner.View.Files["gone.txt"] = original;
        var point = service.Create("baseline", Path.GetFullPath("fixture"), true, "", stop);

        scanner.View = new(new(StringComparer.OrdinalIgnoreCase), new() { [""] = "temporarily unavailable" });
        Assert.Equal(new CheckpointDifference("", DifferenceKind.Unconfirmed, null, "temporarily unavailable"),
            Assert.Single(service.Compare(point.Id, stop).Changes));

        scanner.View = new(new(StringComparer.OrdinalIgnoreCase) { ["keep.txt"] = original }, new());
        Assert.Equal(new CheckpointDifference("gone.txt", DifferenceKind.Deleted, null),
            Assert.Single(service.Compare(point.Id, stop).Changes));
    }

    [Fact]
    public void Compare_UnconfirmedFolderDoesNotHideDeletionFromSimilarlyNamedSibling()
    {
        var repository = new MemoryRepository();
        var scanner = new ScriptedScanner();
        var service = new Checkpoints(repository, scanner, new CopySpy(), TimeProvider.System);
        var stop = TestContext.Current.CancellationToken;
        var hidden = Path.Combine("logs", "nested", "hidden.txt");
        var sibling = Path.Combine("logs-old", "deleted.txt");
        scanner.View.Files[hidden] = new(1, 1, 9);
        scanner.View.Files[sibling] = new(1, 1, 9);
        var point = service.Create("baseline", Path.GetFullPath("fixture"), true, "", stop);

        scanner.View = new(new(StringComparer.OrdinalIgnoreCase), new(StringComparer.OrdinalIgnoreCase) { ["LOGS"] = "unavailable" });
        var differences = service.Compare(point.Id, stop).Changes;
        Assert.Collection(differences,
            x => Assert.Equal(new CheckpointDifference("LOGS", DifferenceKind.Unconfirmed, null, "unavailable"), x),
            x => Assert.Equal(new CheckpointDifference(sibling, DifferenceKind.Deleted, null), x));
    }

    [Fact]
    public void Collect_PassesSelectedFileFingerprintRootAndCancellationToSaver()
    {
        var downloads = new CopySpy();
        var service = new Checkpoints(new MemoryRepository(), new ScriptedScanner(), downloads, TimeProvider.System);
        var stop = TestContext.Current.CancellationToken;
        var source = Path.GetFullPath("source");
        var point = new CheckpointInfo("id", "baseline", source, true, [], DateTimeOffset.UnixEpoch);
        var expected = new Fingerprint(4, 5, 6);
        var relative = Path.Combine("sub", "測定 A.txt");
        var target = Path.GetFullPath("output");
        service.Collect(point, [new(relative, DifferenceKind.Updated, expected), new("removed", DifferenceKind.Deleted, null)], target, stop);

        Assert.Equal(new DownloadRequest(Path.Combine(source, relative), relative, expected, source), Assert.Single(downloads.Requests));
        Assert.Equal(target, downloads.Output);
        Assert.Equal(stop, downloads.Stop);
    }


    [Fact]
    public void Create_CancellationAtEndOfScanDoesNotPersistAnEmptyBaseline()
    {
        using var cts = new CancellationTokenSource();
        var repository = new MemoryRepository();
        var scanner = new ScriptedScanner { OnScan = cts.Cancel };
        var service = new Checkpoints(repository, scanner, new CopySpy(), TimeProvider.System);

        Assert.Throws<OperationCanceledException>(() =>
            service.Create("baseline", Path.GetFullPath("fixture"), true, "", cts.Token));
        Assert.Empty(service.List());
    }

    [Fact]
    public void Compare_CancellationAtEndOfEmptyScanDoesNotReturnSuccess()
    {
        using var cts = new CancellationTokenSource();
        var repository = new MemoryRepository();
        var scanner = new ScriptedScanner();
        var service = new Checkpoints(repository, scanner, new CopySpy(), TimeProvider.System);
        var point = service.Create("baseline", Path.GetFullPath("fixture"), true, "", CancellationToken.None);
        scanner.OnScan = cts.Cancel;

        Assert.Throws<OperationCanceledException>(() => service.Compare(point.Id, cts.Token));
        Assert.Equal(point, Assert.Single(service.List()));
    }

    [Fact]
    public void Collect_CancellationWhileReadingSelectionsDoesNotCallTheSaver()
    {
        using var cts = new CancellationTokenSource();
        var downloads = new CopySpy();
        var service = new Checkpoints(new MemoryRepository(), new ScriptedScanner(), downloads, TimeProvider.System);
        var point = new CheckpointInfo("id", "baseline", Path.GetFullPath("fixture"), true, [], DateTimeOffset.UnixEpoch);

        Assert.Throws<OperationCanceledException>(() =>
            service.Collect(point, Enumerate(), Path.GetFullPath("output"), cts.Token));
        Assert.Null(downloads.Output);
        Assert.Empty(downloads.Requests);

        IEnumerable<CheckpointDifference> Enumerate()
        {
            yield return new("first.txt", DifferenceKind.Added, new(1, 2, 3));
            cts.Cancel();
            yield return new("second.txt", DifferenceKind.Added, new(4, 5, 6));
        }
    }


    [Theory]
    [InlineData(1, true)]
    [InlineData(100, true)]
    [InlineData(101, false)]
    public void Create_EnforcesNormalizedLengthBoundary(int length, bool valid)
    {
        var repository = new MemoryRepository();
        var scanner = new ScriptedScanner();
        int scans = 0;
        scanner.OnScan = () => scans++;
        var service = new Checkpoints(repository, scanner, new CopySpy(), TimeProvider.System);
        string name = new('a', length);
        string root = Path.GetFullPath("fixture");
        if (valid)
        {
            var point = service.Create(" " + name + " ", root, true, "", CancellationToken.None);
            Assert.Equal(name, point.Name);
            Assert.Equal(point, Assert.Single(service.List()));
            Assert.Equal(1, scans);
        }
        else
        {
            Assert.Throws<ArgumentException>(() => service.Create(name, root, true, "", CancellationToken.None));
            Assert.Empty(service.List());
            Assert.Equal(0, scans);
        }
    }

    [Fact]
    public void Create_InvalidRootRuleOrAlreadyCanceledNeverCallsTheScanner()
    {
        var repository = new MemoryRepository();
        var scanner = new ScriptedScanner { OnScan = () => throw new InvalidOperationException("Unexpected scan.") };
        var service = new Checkpoints(repository, scanner, new CopySpy(), TimeProvider.System);
        string root = Path.GetFullPath("fixture");

        Assert.Throws<ArgumentException>(() => service.Create("baseline", "relative", true, "", CancellationToken.None));
        Assert.Throws<ArgumentException>(() => service.Create("baseline", root, true, "logs/report.txt", CancellationToken.None));
        Assert.Throws<OperationCanceledException>(() => service.Create("baseline", root, true, "", new(true)));
        Assert.Empty(service.List());
    }

    [Fact]
    public void Compare_MissingBaselineNeverStartsScanning()
    {
        var scanner = new ScriptedScanner { OnScan = () => throw new InvalidOperationException("Unexpected scan.") };
        var service = new Checkpoints(new MemoryRepository(), scanner, new CopySpy(), TimeProvider.System);
        Assert.Throws<KeyNotFoundException>(() => service.Compare("missing", CancellationToken.None));
    }

    [Fact]
    public void Compare_SortsNamesAndDetectsEitherContentIndicatorWithoutMutatingBaseline()
    {
        var repository = new MemoryRepository();
        var scanner = new ScriptedScanner();
        var service = new Checkpoints(repository, scanner, new CopySpy(), TimeProvider.System);
        var original = new Fingerprint(1, 2, 3);
        foreach (var name in new[] { "Zulu.txt", "Alpha.txt", "gone.txt" }) scanner.View.Files[name] = original;
        string root = Path.GetFullPath("fixture");
        var point = service.Create("baseline", root, true, "", CancellationToken.None);
        scanner.View.Files.Remove("gone.txt");
        scanner.View.Files["Zulu.txt"] = original with { Bytes = 4 };
        scanner.View.Files["ALPHA.TXT"] = original with { Modified = 20 };
        scanner.View.Files["beta.txt"] = original;

        var (info, rows) = service.Compare(point.Id, CancellationToken.None);

        Assert.Equal(point, info);
        Assert.Equal(new[] { "Alpha.txt", "beta.txt", "gone.txt", "Zulu.txt" }, rows.Select(x => x.Relative));
        Assert.Equal(new[] { DifferenceKind.Updated, DifferenceKind.Added, DifferenceKind.Deleted, DifferenceKind.Updated },
            rows.Select(x => x.Kind));
        Assert.Equal(original, repository.Read(point.Id, CancellationToken.None).Files["Zulu.txt"]);
    }

    [Fact]
    public void Collect_UnconfirmedAndDeletedDoNotProduceRequests()
    {
        var downloads = new CopySpy();
        var service = new Checkpoints(new MemoryRepository(), new ScriptedScanner(), downloads, TimeProvider.System);
        var point = new CheckpointInfo("id", "baseline", Path.GetFullPath("fixture"), true, [], DateTimeOffset.UnixEpoch);
        string output = Path.GetFullPath("export");

        Assert.Empty(service.Collect(point,
            [new("sub", DifferenceKind.Unconfirmed, null), new("old.txt", DifferenceKind.Deleted, null)],
            output, CancellationToken.None));
        Assert.Equal(output, downloads.Output);
        Assert.Empty(downloads.Requests);
    }

    [Fact]
    public void Collect_DestinationUnderSourceIsRejectedBeforeCallingSaver()
    {
        var downloads = new CopySpy();
        var service = new Checkpoints(new MemoryRepository(), new ScriptedScanner(), downloads, TimeProvider.System);
        var point = new CheckpointInfo("id", "baseline", Path.GetFullPath("fixture"), true, [], DateTimeOffset.UnixEpoch);
        var changes = new[] { new CheckpointDifference("report.txt", DifferenceKind.Added, new(1, 2, 3)) };

        Assert.Throws<IOException>(() =>
            service.Collect(point, changes, Path.Combine(point.Root, "export"), CancellationToken.None));
        Assert.Throws<OperationCanceledException>(() =>
            service.Collect(point, changes, Path.GetFullPath("export"), new(true)));
        Assert.Null(downloads.Output);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private static void Reject(Action action, string message)
    {
        try { action(); }
        catch (IOException) { return; }
        throw new InvalidOperationException(message);
    }

    private sealed class MemoryRepository : ICheckpointRepository
    {
        private readonly Dictionary<string, (CheckpointInfo Info, Dictionary<string, Fingerprint> Files)> items = [];
        public CheckpointInfo[] List() => items.Values.Select(x => x.Info).ToArray();
        public (CheckpointInfo Info, Dictionary<string, Fingerprint> Files) Read(string id, CancellationToken stop) => items[id];
        public void Save(CheckpointInfo info, Dictionary<string, Fingerprint> files, CancellationToken stop) =>
            items.Add(info.Id, (info, new(files, StringComparer.OrdinalIgnoreCase)));
    }

    private sealed class ScriptedScanner : ICheckpointScanner
    {
        internal CheckpointScan View { get; set; } = new(new(StringComparer.OrdinalIgnoreCase), new(StringComparer.OrdinalIgnoreCase));
        internal Action? OnScan { get; set; }

        public CheckpointScan Scan(CheckpointInfo scope, CancellationToken stop)
        {
            OnScan?.Invoke();
            return View;
        }
    }

    private sealed class CopySpy : ICheckpointDownloads
    {
        internal string[] Paths = [];
        internal DownloadRequest[] Requests = [];
        internal string? Output;
        internal CancellationToken Stop;
        public DownloadResult[] Save(string output, IReadOnlyList<DownloadRequest> requests, CancellationToken stop)
        {
            Requests = requests.ToArray();
            Output = output;
            Stop = stop;
            Paths = requests.Select(x => x.Relative).ToArray();
            return [];
        }
    }
}
