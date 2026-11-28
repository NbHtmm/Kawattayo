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


    [Fact]
    public void Matches_ClearedRegistrationAndNullableFieldsRemainDistinguishable()
    {
        var item = new WatchItem { Path = @"C:\fixture\report.txt" };
        var cached = CachedTarget.Capture(item, null);
        Assert.Null(cached.SnapshotId);
        Assert.Null(cached.Inventory);
        Assert.Empty(cached.Pending!);
        Assert.True(cached.Matches(item));
        item.Path = @"C:\fixture\other.txt";
        Assert.False(cached.Matches(item));
        item.Path = cached.Path;
        item.IsFolder = true;
        Assert.False(cached.Matches(item));
    }

    [Fact]
    public void SamePending_EmptyDictionariesMatchButDifferentKeysWithSameCountDoNot()
    {
        Assert.True(CachedTarget.SamePending(new(), new()));
        var value = new FileChange("A.txt", "更新", s_time);
        Assert.False(CachedTarget.SamePending(new() { ["A.txt"] = value }, new() { ["B.txt"] = value }));
    }

    [Theory]
    [InlineData(-1L, true, false)]
    [InlineData(0L, true, true)]
    [InlineData(0L, false, false)]
    [InlineData(1L, false, true)]
    [InlineData(2147483647L, false, true)]
    [InlineData(2147483648L, true, false)]
    public void Count_EnforcesOptionalZeroAndIntRange(long value, bool allowZero, bool valid)
    {
        if (valid)
            Assert.Equal((int)value, InventoryDb.Count(value, allowZero));
        else
            Assert.Throws<IOException>(() => InventoryDb.Count(value, allowZero));
    }

    [Fact]
    public void Date_RoundTripsPrecisionAndOffsetOrUsesEmptyForMissing()
    {
        var precise = s_time.AddTicks(7);
        string stored = InventoryDb.Date(precise);
        Assert.Equal("2026-11-26T02:13:31.0000007+09:00", stored);
        var read = Assert.IsType<DateTimeOffset>(InventoryDb.Date(stored));
        Assert.True(precise.EqualsExact(read));
        Assert.Equal("", InventoryDb.Date((DateTimeOffset?)null));
        Assert.Null(InventoryDb.Date(""));
    }

    [Fact]
    public void Date_EmptyIsNeverARequiredDateAndMalformedDateIsNotMissing()
    {
        Assert.Throws<IOException>(() => InventoryDb.Date("2026-02-30T02:13:31.0000000+09:00"));
        Assert.Throws<IOException>(() => InventoryDb.RequiredDate(""));
        Assert.Throws<IOException>(() => InventoryDb.RequiredDate("2026-11-26"));
        Assert.True(s_time.EqualsExact(InventoryDb.RequiredDate("2026-11-26T02:13:31.0000000+09:00")));
    }

    [Fact]
    public void ApplyObservations_ExactScopeRestoresUnreadAndComparisonBaseline()
    {
        var original = RestoreFixture();
        var cached = CachedTarget.Capture(original, "snapshot-01");
        var restored = new WatchItem { Recursive = true };
        InventoryDb.ApplyObservations(cached, restored, actualId =>
        {
            Assert.Equal(cached.Id, actualId);
            return original.PendingFiles.Values;
        }, actualSnapshot =>
        {
            Assert.Equal("snapshot-01", actualSnapshot);
            return original.Inventory!;
        });

        Assert.Equal(cached.Observation, CachedObservation.Capture(restored));
        Assert.Equal(original.PendingFiles["A.txt"], Assert.Single(restored.PendingFiles).Value);
        Assert.Same(original.Inventory, restored.Inventory);
    }

    [Fact]
    public void ApplyObservations_ChangedScopeRetainsUnreadButDoesNotLoadOldBaseline()
    {
        var original = RestoreFixture();
        var cached = CachedTarget.Capture(original, "snapshot-01");
        var restored = new WatchItem { Recursive = false };
        InventoryDb.ApplyObservations(cached, restored, _ => original.PendingFiles.Values,
            _ => throw new InvalidOperationException("The previous scope must not load an inventory."));

        Assert.Equal((original.Unread, original.LastChange, original.LastChanged, original.AcknowledgedAt, original.LastModified),
            (restored.Unread, restored.LastChange, restored.LastChanged, restored.AcknowledgedAt, restored.LastModified));
        Assert.Equal(original.PendingFiles["A.txt"], Assert.Single(restored.PendingFiles).Value);
        Assert.Null(restored.Baseline);
        Assert.Null(restored.Fingerprint);
        Assert.Null(restored.DirectoryStamp);
        Assert.Null(restored.Inventory);
        Assert.Null(restored.InventoryExclusions);
        Assert.Null(restored.LatestFileWriteAt);
    }

    [Fact]
    public void ApplyObservations_NoSavedInventoryClearsPreviousInventoryWithoutLoading()
    {
        var original = RestoreFixture();
        var restored = Fixture();
        InventoryDb.ApplyObservations(CachedTarget.Capture(original, null), restored, _ => [],
            _ => throw new InvalidOperationException("There is no snapshot to load."));

        Assert.Equal(ProbeState.Found, restored.Baseline);
        Assert.Null(restored.Inventory);
    }

    [Fact]
    public void ApplyObservations_DuplicateUnreadPathFailsBeforeLoadingSnapshot()
    {
        var original = RestoreFixture();
        var pending = original.PendingFiles["A.txt"];
        var restored = new WatchItem { Recursive = true };
        Assert.Throws<IOException>(() => InventoryDb.ApplyObservations(
            CachedTarget.Capture(original, "snapshot-01"), restored,
            _ => [pending, pending with { Relative = "a.txt" }],
            _ => throw new InvalidOperationException("Invalid pending rows must stop restoration.")));
    }

    [Fact]
    public void ApplyObservations_ReportsSnapshotLoadFailureWithoutReplacingInventory()
    {
        var original = RestoreFixture();
        var restored = new WatchItem { Recursive = true };
        var failure = new IOException("missing snapshot");
        Assert.Same(failure, Assert.Throws<IOException>(() => InventoryDb.ApplyObservations(
            CachedTarget.Capture(original, "snapshot-01"), restored, _ => original.PendingFiles.Values,
            _ => throw failure)));
        Assert.Null(restored.Inventory);
    }

    [Fact]
    public void ApplyObservations_PendingReadFailureStopsBeforeLoadingSnapshot()
    {
        var original = RestoreFixture();
        var restored = new WatchItem { Recursive = true };
        var failure = new IOException("cannot read pending changes");
        Assert.Same(failure, Assert.Throws<IOException>(() => InventoryDb.ApplyObservations(
            CachedTarget.Capture(original, "snapshot-01"), restored, _ => throw failure,
            _ => throw new InvalidOperationException("Unread entries must be read before the snapshot."))));
        Assert.Null(restored.Baseline);
        Assert.Null(restored.Inventory);
    }

    private static WatchItem RestoreFixture()
    {
        var item = Fixture();
        item.Id = Guid.NewGuid();
        item.LastChange = "updated";
        item.LastChanged = s_time;
        item.AcknowledgedAt = s_time.AddHours(-1);
        item.LastModified = s_time.AddHours(-2);
        item.Baseline = ProbeState.Found;
        item.Fingerprint = new(1, 2, 3);
        item.DirectoryStamp = 55;
        item.InventoryExclusions = FileExclusions.Normalize(["*.tmp", "~$*"]);
        item.LatestFileWriteAt = s_time.AddHours(-3);
        return item;
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
