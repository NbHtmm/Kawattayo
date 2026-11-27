using ShareFileWatch;
using Xunit;

public sealed class InventoryDbWatchCacheTests
{
    private static readonly DateTimeOffset s_time = new(2026, 11, 26, 2, 13, 31, TimeSpan.FromHours(9));

    [Fact]
    public void Capture_RecordsObservationAndScopeWithoutVolatileScanState()
    {
        var fingerprint = new Fingerprint(100, 50, 37);
        var item = new WatchItem
        {
            Baseline = ProbeState.Found, Fingerprint = fingerprint, DirectoryStamp = 919,
            InventoryExclusions = ["~$*", "*.tmp"], LastChanged = s_time, AcknowledgedAt = s_time.AddSeconds(-1),
            LatestFileWriteAt = s_time.AddDays(-1), LastModified = s_time.AddDays(-2), LastChange = "更新", Unread = 4,
            Error = "transient", LastChecked = s_time, QueryMs = 150
        };
        Assert.Equal(new CachedObservation(ProbeState.Found, fingerprint, 919, "~$*\n*.tmp", s_time,
            s_time.AddSeconds(-1), s_time.AddDays(-1), s_time.AddDays(-2), "更新", 4), CachedObservation.Capture(item));

        var observed = CachedObservation.Capture(item);
        item.Error = null;
        item.LastChecked = s_time.AddSeconds(5);
        item.QueryMs = 950;
        Assert.Equal("", CachedObservation.Capture(new WatchItem()).Scope);
        Assert.Equal(observed, CachedObservation.Capture(item));
    }

    [Fact]
    public void Capture_KeepsImmutableInventoryReferenceButCopiesPendingDetails()
    {
        var item = Fixture();
        var saved = CachedTarget.Capture(item, "shared-snapshot");
        Assert.Equal((item.Id, item.Path, item.IsFolder, item.Recursive, "shared-snapshot"),
            (saved.Id, saved.Path, saved.IsFolder, saved.Recursive, saved.SnapshotId));
        Assert.Same(item.Inventory, saved.Inventory);
        Assert.Equal(CachedObservation.Capture(item), saved.Observation);
        item.PendingFiles.Clear();
        Assert.Equal("A.txt", Assert.Single(Assert.IsType<Dictionary<string, FileChange>>(saved.Pending)).Key);
    }

    [Fact]
    public void MatchesRegistration_IgnoresPathCaseButNotKindOrDifferentPath()
    {
        var item = Fixture();
        var saved = CachedTarget.Capture(item, "snapshot");
        item.Path = item.Path.ToLowerInvariant();
        Assert.True(saved.MatchesRegistration(item));
        item.IsFolder = false;
        Assert.False(saved.MatchesRegistration(item));
        item.IsFolder = true;
        item.Path += "-other";
        Assert.False(saved.MatchesRegistration(item));
    }

    [Fact]
    public void Matches_IgnoresVolatileProgressButDetectsScopeAndPendingChanges()
    {
        var item = Fixture();
        var saved = CachedTarget.Capture(item, "snapshot");
        Assert.True(saved.Matches(item));
        item.LastChecked = s_time.AddMinutes(1);
        item.QueryMs = 200;
        Assert.True(saved.Matches(item));

        item.Recursive = false;
        Assert.False(saved.Matches(item));
        item.Recursive = true;
        item.PendingFiles["A.txt"] = item.PendingFiles["A.txt"] with { Count = 3 };
        Assert.False(saved.Matches(item));
        item.PendingFiles["A.txt"] = saved.Pending!["A.txt"];
        item.Unread = 3;
        Assert.False(saved.Matches(item));
        item.Unread = 2;
        item.Inventory = new(item.Inventory!, StringComparer.OrdinalIgnoreCase);
        Assert.False(saved.Matches(item));
    }

    [Fact]
    public void SamePending_RequiresExactContentAndCountButNotInsertionOrder()
    {
        var first = new FileChange("left/same.txt", "更新", s_time, Count: 2);
        var second = new FileChange("right/same.txt", "追加", s_time.AddSeconds(1));
        var a = new Dictionary<string, FileChange> { [first.Relative] = first, [second.Relative] = second };
        var b = new Dictionary<string, FileChange> { [second.Relative] = second, [first.Relative] = first };
        Assert.True(CachedTarget.SamePending(a, b));
        Assert.False(CachedTarget.SamePending(null, new()));
        b[first.Relative] = first with { DetectedAt = s_time.AddSeconds(2) };
        Assert.False(CachedTarget.SamePending(a, b));
        b.Remove(first.Relative);
        Assert.False(CachedTarget.SamePending(a, b));
    }

    private static WatchItem Fixture() => new()
    {
        Path = @"C:\Fixture\計測",
        IsFolder = true,
        Recursive = true,
        Unread = 2,
        Inventory = new(StringComparer.OrdinalIgnoreCase) { ["A.txt"] = new(1, 2, 3) },
        PendingFiles = new(StringComparer.OrdinalIgnoreCase) { ["A.txt"] = new("A.txt", "更新", s_time, Count: 2) }
    };
}
