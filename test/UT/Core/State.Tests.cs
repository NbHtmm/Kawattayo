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

        item.SetRecursive(false);
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

    private static Dictionary<string, Fingerprint> Snapshot(params (string Path, Fingerprint Data)[] entries) =>
        entries.ToDictionary(entry => entry.Path, entry => entry.Data, StringComparer.OrdinalIgnoreCase);
}
