using ShareFileWatch;
using Xunit;

public sealed class WindowsWatchProbeTests
{
    private static readonly DateTimeOffset s_now = new(2026, 1, 10, 15, 0, 0, TimeSpan.Zero);
    private static readonly WindowsWatchProbe.Backend s_closed = new(
        _ => throw new InvalidOperationException("Unexpected file read"),
        (_, _, _, _) => throw new InvalidOperationException("Unexpected full read"),
        (_, _, _, _, _, _) => throw new InvalidOperationException("Unexpected delta read"),
        _ => throw new InvalidOperationException("Unexpected timestamp query"), () => s_now);

    [Theory]
    [InlineData("found")]
    [InlineData("missing")]
    [InlineData("unavailable")]
    public void Read_OrdinaryFileObservationDoesNotQueryFolderState(string kind)
    {
        var actual = kind switch { "found" => new ProbeResult(ProbeState.Found, new(1, 1, 12)),
            "missing" => new(ProbeState.Missing), _ => new(ProbeState.Unavailable, Error: "offline") };
        var request = Request(folder: false, watchStamp: true);
        var backend = s_closed with { SingleFile = path => { Assert.Equal(request.Path, path); return actual; } };
        var observation = new WindowsWatchProbe(_ => false, backend).Read(request, TestContext.Current.CancellationToken, baseline: [], paths: []);
        Assert.Equal((request.Id, request.Revision, s_now), (observation.Id, observation.Revision, observation.At));
        Assert.Same(actual, observation.File);
        Assert.Null(observation.Folder);
        Assert.Null(observation.PreparedChanges);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Read_FullReadIsRequiredUnlessBothAnInventoryAndAnEventListAreAvailable(bool hasInventory, bool hasEvents)
    {
        var request = Request(folder: true, watchStamp: false);
        var checkedPaths = new List<string>();
        string owned = Path.Combine(request.Path, "owned.dat");
        var backend = s_closed with { FullFolder = (path, recursive, excludes, token) =>
        {
            Assert.Equal(request.Path, path);
            Assert.True(recursive);
            Assert.True(token.CanBeCanceled);
            Assert.True(excludes(Path.Combine(path, "ignored.TMP")));
            Assert.True(excludes(owned));
            Assert.False(excludes(Path.Combine(path, "valid.csv")));
            return new(new() { ["valid.csv"] = new(1, 1, 2) });
        } };
        using var stop = new CancellationTokenSource();
        var observation = new WindowsWatchProbe(path => { checkedPaths.Add(path); return path == owned; }, backend)
            .Read(request, stop.Token, hasInventory ? [] : null, hasEvents ? [] : null);
        Assert.Null(observation.File);
        Assert.Same(request.Exclusions, observation.Folder!.Exclusions);
        Assert.Null(observation.Folder.DirectoryStamp);
        Assert.Equal(new[] { owned, Path.Combine(request.Path, "valid.csv") }, checkedPaths);
        if (hasInventory) Assert.Equal("追加", Assert.Single(observation.PreparedChanges!).Kind);
        else Assert.Null(observation.PreparedChanges);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Read_DeltaForwardsScopeAndOnlyReportsRealChangesWhenExclusionRulesChange(bool exclusionsChanged)
    {
        using var stop = new CancellationTokenSource();
        var request = Request(folder: true, watchStamp: true);
        var stamp = new Fingerprint(10, 1, 20);
        var baseline = new Dictionary<string, Fingerprint>(StringComparer.OrdinalIgnoreCase)
        {
            ["updated.csv"] = stamp,
            ["gone.tmp"] = stamp
        };
        string[] paths = [Path.Combine(request.Path, "updated.csv")];
        var backend = s_closed with
        {
            FolderDelta = (path, recursive, prior, events, excludes, token) =>
            {
                Assert.Equal(request.Path, path);
                Assert.True(recursive);
                Assert.Same(baseline, prior);
                Assert.Same(paths, events);
                Assert.True(excludes(Path.Combine(path, "gone.tmp")));
                Assert.Equal(stop.Token, token);
                return new(new(StringComparer.OrdinalIgnoreCase) { ["updated.csv"] = stamp with { Bytes = 30 } });
            },
            DirectoryStamp = path => { Assert.Equal(request.Path, path); return 95; }
        };
        var observation = new WindowsWatchProbe(_ => false, backend)
            .Read(request, stop.Token, baseline, paths, exclusionsChanged ? [] : ["*.TMP"]);
        Assert.Equal(95, observation.Folder!.DirectoryStamp);
        Assert.Same(request.Exclusions, observation.Folder.Exclusions);
        Assert.Contains(observation.PreparedChanges!, change => change.Relative == "updated.csv" && change.Kind == "更新" && change.DetectedAt == s_now);
        Assert.Equal(exclusionsChanged ? 1 : 2, observation.PreparedChanges!.Count);
    }

    [Theory]
    [InlineData("our-cache")]
    [InlineData("no-inventory")]
    [InlineData("io")]
    [InlineData("access")]
    public void Read_TimestampFailureOrSuppressionNeverInventsFolderUpdates(string mode)
    {
        var request = Request(folder: true, watchStamp: true);
        var backend = s_closed with {
            FullFolder = (_, _, _, _) => mode == "no-inventory" ? new(null, "folder offline") : new(new()),
            DirectoryStamp = _ => mode switch { "io" => throw new IOException("stamp failure"),
                "access" => throw new UnauthorizedAccessException("stamp failure"), _ => throw new InvalidOperationException("timestamp must be suppressed") }
        };
        var observation = new WindowsWatchProbe(path => mode == "our-cache" && path == Path.Combine(request.Path, AppDefaults.SettingsFileName), backend)
            .Read(request, TestContext.Current.CancellationToken, baseline: []);
        Assert.Null(observation.Folder!.DirectoryStamp);
        if (mode == "our-cache")
        {
            Assert.Empty(observation.Folder.Files!);
            Assert.Empty(observation.PreparedChanges!);
        }
        else
        {
            Assert.Null(observation.Folder.Files);
            Assert.Null(observation.PreparedChanges);
            Assert.Equal(mode == "no-inventory" ? "folder offline" : "stamp failure", observation.Folder.Error);
        }
    }

    private static WatchScanRequest Request(bool folder, bool watchStamp) =>
        new(Guid.NewGuid(), folder ? @"C:\fixture\folder" : @"C:\fixture\file.txt", folder, true, 24, ["*.tmp"], watchStamp);

    [Theory]
    [InlineData("settings.json")]
    [InlineData("cache.sqlite3-wal")]
    [InlineData("settings.json.broken-0123456789abcdef0123456789abcdef")]
    public void Read_AppOwnedFileIsUnavailableAndKeepsTargetIdentity(string name)
    {
        var root = Path.GetFullPath("test-distribution");
        var owned = new AppOwnedFiles(Path.Combine(root, "settings.json"), Path.Combine(root, "cache.sqlite3"));
        var checkedPaths = new List<string>();
        var probe = new WindowsWatchProbe(path =>
        {
            checkedPaths.Add(path);
            // The app-owned shortcut must not require a real file or any Win32 reads.
            return owned.Contains(path);
        });
        var request = new WatchScanRequest(Guid.NewGuid(), Path.Combine(root, name), false, false, 27, [], false);

        var observation = probe.Read(request, TestContext.Current.CancellationToken);
        Assert.Equal(request.Id, observation.Id);
        Assert.Equal(request.Revision, observation.Revision);
        Assert.Equal(request.Path, Assert.Single(checkedPaths));
        Assert.Null(observation.Folder);
        Assert.Null(observation.PreparedChanges);
        var file = Assert.IsType<ProbeResult>(observation.File);
        Assert.Equal(ProbeState.Unavailable, file.State);
        Assert.Null(file.Fingerprint);
        Assert.False(string.IsNullOrWhiteSpace(file.Error));
    }
}
