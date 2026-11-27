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
        public CheckpointScan Scan(CheckpointInfo scope, CancellationToken stop) => View;
    }

    private sealed class CopySpy : ICheckpointDownloads
    {
        internal string[] Paths = [];
        public DownloadResult[] Save(string output, IReadOnlyList<DownloadRequest> requests, CancellationToken stop)
        {
            Paths = requests.Select(x => x.Relative).ToArray();
            return [];
        }
    }
}
