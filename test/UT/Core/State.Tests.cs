using ShareFileWatch;
using Xunit;

public sealed class StateTests
{
    private static readonly DateTimeOffset s_now = new(2026, 1, 2, 8, 0, 0, TimeSpan.Zero);
    private static readonly Fingerprint s_first = new(s_now.AddMinutes(-3).ToFileTime(), 1, 10);
    private static readonly Fingerprint s_second = new(s_now.AddMinutes(-2).ToFileTime(), 1, 11);

    [Fact]
    public void Observe_UnavailablePreservesBaselineAndRecoveryIsAnUpdate()
    {
        var item = new WatchItem { Path = "file.txt" };
        var events = new List<(string, string)>();
        void Record(string path, string change) => events.Add((path, change));
        Assert.Null(item.Observe(new(ProbeState.Found, s_first), s_now, Record));
        Assert.Null(item.Observe(new(ProbeState.Unavailable, Error: "not connected"), s_now, Record));
        Assert.Equal("not connected", item.Error);
        Assert.Equal(s_first, item.Fingerprint);
        Assert.Equal(ProbeState.Found, item.Baseline);
        Assert.Empty(events);

        Assert.Equal("更新", item.Observe(new(ProbeState.Found, s_second), s_now, Record));
        Assert.Null(item.Error);
        Assert.Equal(s_now.AddMinutes(-2), item.LastModified);
        Assert.Equal(("file.txt", "更新"), Assert.Single(events));
    }

    [Fact]
    public void Observe_DisablingDeletionUpdatesBaselineWithoutPublishingDeletion()
    {
        var item = new WatchItem();
        item.Observe(new(ProbeState.Found, s_first), s_now);
        Assert.Null(item.Observe(new(ProbeState.Missing), s_now, notifyDeleted: false));
        Assert.Equal(ProbeState.Missing, item.Baseline);
        Assert.Equal(0, item.Unread);
        Assert.Equal("出現", item.Observe(new(ProbeState.Found, s_first), s_now));
        Assert.Equal(1, item.Unread);
    }

    [Fact]
    public void ObserveFolder_UnavailablePreservesInventoryAndDoesNotInventDeletions()
    {
        var item = new WatchItem { Path = "watched", IsFolder = true };
        var before = Snapshot(("active.txt", s_first));
        Assert.Null(item.ObserveFolder(new(before), s_now));
        Assert.Null(item.ObserveFolder(new(null, Error: "offline"), s_now));
        Assert.Same(before, item.Inventory);
        Assert.Equal("offline", item.Error);
        Assert.Empty(item.PendingFiles);
        Assert.NotNull(item.ObserveFolder(new(Snapshot(("active.txt", s_second))), s_now));
        Assert.Null(item.Error);
        Assert.Equal("更新", Assert.Single(item.PendingFiles).Value.Kind);
    }

    [Fact]
    public void SetRecursive_OnlyARealScopeChangeResetsBaseline()
    {
        var item = new WatchItem { IsFolder = true, Recursive = true };
        var before = Snapshot(("active.txt", s_first));
        item.ObserveFolder(new(before), s_now);
        item.SetRecursive(true);
        Assert.Equal(0, item.Revision);
        Assert.Same(before, item.Inventory);

        item.Error = "transient";
        item.SetRecursive(false);
        Assert.Null(item.Error);
        Assert.Equal(1, item.Revision);
        Assert.False(item.Recursive);
        Assert.Null(item.Inventory);
        Assert.Null(item.Baseline);
        Assert.Null(item.ObserveFolder(new(Snapshot(("other.txt", s_first))), s_now));
        Assert.Equal(0, item.Unread);
    }

    [Fact]
    public void AcknowledgeFile_DoesNotEraseNewerChangeBasedOnOldSnapshot()
    {
        var item = new WatchItem { IsFolder = true };
        item.ObserveFolder(new(Snapshot()), s_now);
        item.ObserveFolder(new(Snapshot(("file.txt", s_first))), s_now);
        var old = item.PendingFiles["file.txt"];
        item.ObserveFolder(new(Snapshot(("file.txt", s_second))), s_now.AddSeconds(1));
        Assert.Equal(2, item.PendingFiles["file.txt"].Count);
        item.AcknowledgeFile(old);
        Assert.Equal(2, item.Unread);
        Assert.Null(item.AcknowledgedAt);
        item.AcknowledgeFile(item.PendingFiles["file.txt"]);
        Assert.Empty(item.PendingFiles);
        Assert.Equal(0, item.Unread);
        Assert.NotNull(item.AcknowledgedAt);
    }

    [Fact]
    public void Acknowledge_ClearsAllPendingWithoutChangingBaselineOrRepeatTimestamp()
    {
        var item = new WatchItem { IsFolder = true };
        item.ObserveFolder(new(Snapshot()), s_now);
        var observed = Snapshot(("file.txt", s_second));
        item.ObserveFolder(new(observed), s_now);
        item.Acknowledge();
        Assert.Equal(0, item.Unread);
        Assert.Empty(item.PendingFiles);
        Assert.Same(observed, item.Inventory);
        var firstAck = item.AcknowledgedAt;
        Assert.NotNull(firstAck);
        item.Acknowledge();
        Assert.Equal(firstAck, item.AcknowledgedAt);
    }

    [Fact]
    public void CaptureForSave_CopiesPendingAndHistoryButRetainsImmutableInventory()
    {
        var target = new WatchItem { IsFolder = true, Unread = 4, Inventory = Snapshot(("file.txt", s_first)) };
        target.PendingFiles["file.txt"] = new("file.txt", "追加", s_now, Count: 2);
        var state = new AppState { Items = [target], History = new([new(s_now, "file.txt", "追加")]) };
        var copy = state.CaptureForSave();
        Assert.Equal(2, copy.Items[0].SavedUnreadWithoutDetails);
        target.PendingFiles.Clear();
        state.History.Clear();
        Assert.Single(copy.Items[0].PendingFiles);
        Assert.Single(copy.History);
        Assert.Same(target.Inventory, copy.Items[0].Inventory);
    }

    [Fact]
    public void ObserveFolder_SuppressedDeletionAdvancesBaselineAndReappearanceIsNew()
    {
        var item = new WatchItem { Path = "watched", IsFolder = true };
        var events = new List<(string, string)>();
        void Record(string path, string kind) => events.Add((path, kind));
        item.ObserveFolder(new(Snapshot(("log.txt", s_first))), s_now);
        var empty = Snapshot();

        Assert.Null(item.ObserveFolder(new(empty), s_now, Record, notifyDeleted: false));
        Assert.Same(empty, item.Inventory);
        Assert.Null(item.LatestFileWriteAt);
        Assert.Empty(events);
        Assert.Equal(0, item.Unread);

        Assert.NotNull(item.ObserveFolder(new(Snapshot(("log.txt", s_first))), s_now, Record, notifyDeleted: false));
        Assert.Equal((System.IO.Path.Combine("watched", "log.txt"), "追加"), Assert.Single(events));
        Assert.Equal(1, item.Unread);
    }

    [Fact]
    public void ObserveFolder_ChangingExclusionsDoesNotInventEvents()
    {
        var item = new WatchItem { IsFolder = true };
        var log = Snapshot(("keep.log", s_first), ("~$lock.docx", s_second));
        item.ObserveFolder(new(log), s_now);

        var excludesLock = new[] { "~$*" };
        var filtered = Snapshot(("keep.log", s_first));
        Assert.Null(item.ObserveFolder(new(filtered) { Exclusions = excludesLock }, s_now));
        Assert.Same(filtered, item.Inventory);
        Assert.Equal(excludesLock, item.InventoryExclusions);
        Assert.Equal(s_now.AddMinutes(-3), item.LatestFileWriteAt);

        Assert.Null(item.ObserveFolder(new(log), s_now));
        Assert.Same(log, item.Inventory);
        Assert.Empty(item.InventoryExclusions!);
        Assert.Equal(s_now.AddMinutes(-2), item.LatestFileWriteAt);
        Assert.Equal(0, item.Unread);
    }

    [Fact]
    public void ObserveFolder_DirectoryStampChangeIsPublishedEvenWithoutFileChanges()
    {
        var item = new WatchItem { IsFolder = true };
        var inventory = Snapshot(("log.txt", s_first));
        item.ObserveFolder(new(inventory) { DirectoryStamp = s_first.Modified }, s_now);

        Assert.Null(item.ObserveFolder(new(inventory) { DirectoryStamp = s_first.Modified }, s_now));
        Assert.NotNull(item.ObserveFolder(new(inventory) { DirectoryStamp = s_second.Modified }, s_now));
        var pending = Assert.Single(item.PendingFiles);
        Assert.Equal(".", pending.Key);
        Assert.Equal("更新", pending.Value.Kind);
        Assert.Equal(s_now.AddMinutes(-2), pending.Value.ModifiedAt);
        Assert.Equal(s_now, item.LastChanged);
        Assert.Equal(1, item.Unread);
    }

    [Fact]
    public void ObserveFolder_UnreadAndPendingCountsSaturateInsteadOfOverflowing()
    {
        var item = new WatchItem { IsFolder = true, Unread = int.MaxValue };
        item.ObserveFolder(new(Snapshot(("log.txt", s_first))), s_now);
        item.PendingFiles["log.txt"] = new("log.txt", "更新", s_now.AddSeconds(-1), Count: int.MaxValue);

        Assert.NotNull(item.ObserveFolder(new(Snapshot(("log.txt", s_second))), s_now));
        Assert.Equal(int.MaxValue, item.Unread);
        Assert.Equal(int.MaxValue, item.PendingFiles["log.txt"].Count);
        Assert.Equal(0, item.SavedUnreadWithoutDetails);
    }


    [Fact]
    public void AcknowledgeFile_LeavesOtherChildrenAndLegacyUnreadOutsideThatDetail()
    {
        var item = new WatchItem { IsFolder = true, Unread = 8, LatestFileWriteAt = s_now };
        var left = new FileChange(Path.Combine("left", "same.txt"), "更新", s_now, Count: 3);
        var right = new FileChange(Path.Combine("right", "same.txt"), "追加", s_now, Count: 2);
        item.PendingFiles[left.Relative] = left;
        item.PendingFiles[right.Relative] = right;
        Assert.Equal(3, item.SavedUnreadWithoutDetails);

        item.AcknowledgeFile(left with { Relative = Path.Combine("other", "same.txt") });
        Assert.Equal(8, item.Unread);
        Assert.Null(item.AcknowledgedAt);

        item.AcknowledgeFile(left);
        Assert.Equal(right, Assert.Single(item.PendingFiles).Value);
        Assert.Equal(5, item.Unread);
        Assert.Equal(3, item.SavedUnreadWithoutDetails);
        Assert.Equal(s_now, item.LatestFileWriteAt);
        Assert.NotNull(item.AcknowledgedAt);
    }

    [Fact]
    public void ObserveFolder_SuppressionDoesNotLoseRealUpdatesDuringMixedChanges()
    {
        var item = new WatchItem { IsFolder = true, Path = "watched" };
        var before = Snapshot(("deleted.txt", s_first), ("updated.txt", s_first));
        item.ObserveFolder(new(before), s_now);
        var after = Snapshot(("updated.txt", s_second), ("new.txt", s_first));
        var observed = new List<(string Path, string Change)>();

        item.ObserveFolder(new(after), s_now.AddSeconds(1), (path, kind) => observed.Add((path, kind)), notifyDeleted: false);

        Assert.Same(after, item.Inventory);
        Assert.Equal(2, item.Unread);
        Assert.DoesNotContain("deleted.txt", item.PendingFiles.Keys);
        Assert.Equal("更新", item.PendingFiles["updated.txt"].Kind);
        Assert.Equal("追加", item.PendingFiles["new.txt"].Kind);
        Assert.Equal(2, observed.Count);
        Assert.DoesNotContain(observed, entry => entry.Change == "消失");
        Assert.Null(item.ObserveFolder(new(after), s_now.AddSeconds(2)));
        Assert.Equal(2, item.Unread);
    }


    [Fact]
    public void Observe_TransitionsThroughMissingUnchangedAndSizeOnlyUpdate()
    {
        var item = new WatchItem { Path = "only.txt" };
        var events = new List<string>();
        void Record(string _, string kind) => events.Add(kind);
        Assert.Null(item.Observe(new(ProbeState.Missing), s_now, Record));
        Assert.Null(item.Observe(new(ProbeState.Unavailable), s_now, Record));
        Assert.Equal("確認不可", item.Error);
        Assert.Equal(ProbeState.Missing, item.Baseline);
        Assert.Null(item.Observe(new(ProbeState.Missing), s_now, Record));
        Assert.Null(item.Error);
        Assert.Equal("出現", item.Observe(new(ProbeState.Found, s_first), s_now, Record));
        Assert.Null(item.Observe(new(ProbeState.Found, s_first), s_now, Record));
        Assert.Equal("更新", item.Observe(new(ProbeState.Found, s_first with { Bytes = 12 }), s_now, Record));
        Assert.Null(item.LastModified);
        Assert.Equal(s_now.AddMinutes(-3), item.LatestFileWriteAt);
        Assert.Equal("消失", item.Observe(new(ProbeState.Missing), s_now, Record));
        Assert.Equal(new[] { "出現", "更新", "消失" }, events);
        Assert.Equal(3, item.Unread);
        Assert.Null(item.Fingerprint);
        Assert.Equal(s_now.AddMinutes(-3), item.LatestFileWriteAt);
    }

    [Fact]
    public void ObserveFolder_UsesPreparedChangesAndRestoresMissingLatestTimestamp()
    {
        var item = new WatchItem { Path = "watched", IsFolder = true };
        var inventory = Snapshot(("log.txt", s_first));
        Assert.Null(item.ObserveFolder(new(inventory), s_now));
        item.LatestFileWriteAt = null;

        Assert.Null(item.ObserveFolder(new(inventory), s_now, preparedChanges: []));
        Assert.Equal(s_now.AddMinutes(-3), item.LatestFileWriteAt);
        Assert.Equal(0, item.Unread);

        var change = new FileChange("log.txt", "更新", s_now, s_now.AddMinutes(-2));
        Assert.NotNull(item.ObserveFolder(new(Snapshot(("log.txt", s_second))), s_now, preparedChanges: [change]));
        Assert.Equal(change, Assert.Single(item.PendingFiles).Value);
        Assert.Equal(s_now.AddMinutes(-2), item.LatestFileWriteAt);
    }

    [Fact]
    public void ObserveFolder_DefaultUnavailableMessageAndTimestampSentinelDoNotInventUpdates()
    {
        var item = new WatchItem { IsFolder = true };
        var snapshot = Snapshot(("log.txt", new Fingerprint(0, 0, 4)));
        Assert.Null(item.ObserveFolder(new(snapshot) { DirectoryStamp = 0 }, s_now));
        Assert.Null(item.LatestFileWriteAt);
        Assert.Null(item.ObserveFolder(new(null), s_now));
        Assert.Equal("フォルダを確認できません", item.Error);
        Assert.Same(snapshot, item.Inventory);
        Assert.Null(item.ObserveFolder(new(snapshot), s_now));
        Assert.Null(item.Error);
        Assert.Null(item.DirectoryStamp);
        Assert.Empty(item.PendingFiles);
    }

    [Fact]
    public void AcknowledgeFile_StaleCountDoesNotUnderflowAndDetailsUseLongSum()
    {
        var item = new WatchItem { Unread = 2 };
        var entry = new FileChange("first.txt", "更新", s_now, Count: int.MaxValue);
        item.PendingFiles[entry.Relative] = entry;
        item.PendingFiles["second.txt"] = new("second.txt", "更新", s_now, Count: int.MaxValue);
        Assert.Equal(0, item.SavedUnreadWithoutDetails);
        item.AcknowledgeFile(entry);
        Assert.Equal(0, item.Unread);
        Assert.Equal("second.txt", Assert.Single(item.PendingFiles).Key);
        Assert.NotNull(item.AcknowledgedAt);
    }

    [Fact]
    public void CaptureForSave_EmptyStateAndTargetPreserveConfigurationAndCopyCollections()
    {
        var state = new AppState { DarkMode = true, Paused = true, PollSeconds = 17 };
        var initial = state.CaptureForSave();
        Assert.Empty(initial.Items);
        Assert.Empty(initial.History);
        Assert.NotSame(state.Items, initial.Items);
        Assert.NotSame(state.History, initial.History);
        Assert.Equal((true, true, 17), (initial.DarkMode, initial.Paused, initial.PollSeconds));
        var target = new WatchItem { Id = Guid.NewGuid(), Path = "target", IsFolder = false, Unread = 3 };
        var copy = target.CaptureForSave();
        Assert.Equal((target.Id, "target", false, 3), (copy.Id, copy.Path, copy.IsFolder, copy.Unread));
        Assert.NotSame(target.PendingFiles, copy.PendingFiles);
        Assert.Same(target.PendingFiles.Comparer, copy.PendingFiles.Comparer);
        Assert.Null(copy.Inventory);
        Assert.Equal(3, target.SavedUnreadWithoutDetails);
    }


    [Fact]
    public void ObserveFolder_ReportsDeletionAndNullLatestTimeWithoutMutatingBaselineOnUnchangedScan()
    {
        var item = new WatchItem { Path = "watched", IsFolder = true };
        var original = Snapshot(("log.txt", s_first));
        item.ObserveFolder(new(original), s_now);
        var equivalent = Snapshot(("log.txt", s_first));
        Assert.Null(item.ObserveFolder(new(equivalent), s_now.AddSeconds(1)));
        Assert.Same(original, item.Inventory);
        Assert.Equal(s_now.AddSeconds(1), item.LastChecked);

        var removed = Snapshot();
        Assert.Contains("消失", item.ObserveFolder(new(removed), s_now.AddSeconds(2), notifyDeleted: true)!);
        Assert.Equal("消失", Assert.Single(item.PendingFiles).Value.Kind);
        Assert.Null(item.LatestFileWriteAt);
        Assert.Same(removed, item.Inventory);
        Assert.Equal(1, item.Unread);
    }

    private static Dictionary<string, Fingerprint> Snapshot(params (string Path, Fingerprint Data)[] entries) =>
        entries.ToDictionary(entry => entry.Path, entry => entry.Data, StringComparer.OrdinalIgnoreCase);
}
