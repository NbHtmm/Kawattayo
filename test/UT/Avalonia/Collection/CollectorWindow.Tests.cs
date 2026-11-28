using ShareFileWatch;
using Xunit;

public sealed class CollectorWindowTests
{
    private static readonly CheckpointInfo s_before = new("a", "before", @"C:\fixture", true, [], DateTimeOffset.UnixEpoch);
    private static readonly CheckpointDifference s_added = new("added.txt", DifferenceKind.Added, null);
    private static readonly CheckpointDifference s_updated = new("updated.txt", DifferenceKind.Updated, null);
    private static readonly CheckpointDifference s_missing = new("missing.txt", DifferenceKind.Deleted, null);
    private static readonly CheckpointDifference s_unknown = new("offline", DifferenceKind.Unconfirmed, null, "offline");

    [Fact]
    public async Task Compare_CurrentSuccessClearsOldPreviewAndPublishesNewResult()
    {
        var stop = TestContext.Current.CancellationToken;
        var snapshot = s_before with { Name = "persisted name" };
        CheckpointDifference[] changes = [s_added, s_missing, s_unknown];
        var operations = new List<string>();
        await CollectorWindow.Compare(s_before, () => operations.Add("clear"), (id, token) =>
        {
            Assert.Equal("a", id);
            Assert.Equal(stop, token);
            operations.Add("scan");
            return Task.FromResult((snapshot, changes));
        }, () => "a", (info, items, message) =>
        {
            operations.Add("publish");
            Assert.Same(snapshot, info);
            Assert.Same(changes, items);
            Assert.Equal("差分・確認不能：3件（確認不能 1件）", message);
        }, stop);
        Assert.Equal(new[] { "clear", "scan", "publish" }, operations);
    }

    [Fact]
    public async Task Compare_NoSelectionDoesNotClearOrStartScan()
    {
        await CollectorWindow.Compare(null, () => throw new InvalidOperationException(), (_, _) => throw new InvalidOperationException(),
            () => throw new InvalidOperationException(), (_, _, _) => throw new InvalidOperationException(), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Compare_CurrentEmptyResultPublishesZeroCounts()
    {
        bool published = false;
        await CollectorWindow.Compare(s_before, () => { }, (_, _) => Task.FromResult((s_before, Array.Empty<CheckpointDifference>())),
            () => "a", (_, items, message) =>
            {
                Assert.Empty(items);
                Assert.Equal("差分・確認不能：0件（確認不能 0件）", message);
                published = true;
            }, TestContext.Current.CancellationToken);
        Assert.True(published);
    }

    [Fact]
    public async Task Compare_ScanErrorDoesNotPublishPartialResult()
    {
        var failure = new IOException("offline");
        bool cleared = false;
        var actual = await Assert.ThrowsAsync<IOException>(() => CollectorWindow.Compare(s_before, () => cleared = true,
            (_, _) => Task.FromException<(CheckpointInfo, CheckpointDifference[])>(failure),
            () => "a", (_, _, _) => throw new InvalidOperationException(), TestContext.Current.CancellationToken));
        Assert.Same(failure, actual);
        Assert.True(cleared);
    }

    [Fact]
    public async Task Compare_LateSuccessfulScanAfterCancellationCannotPublish()
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var scanning = new TaskCompletionSource<(CheckpointInfo, CheckpointDifference[])>(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = CollectorWindow.Compare(s_before, () => { }, (_, _) => scanning.Task,
            () => "a", (_, _, _) => throw new InvalidOperationException(), stop.Token);
        stop.Cancel();
        scanning.SetResult((s_before, [s_added]));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
    }

    [Fact]
    public async Task Compare_AlreadyCanceledDoesNotClearOrStartScan()
    {
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CollectorWindow.Compare(s_before,
            () => throw new InvalidOperationException(), (_, _) => throw new InvalidOperationException(),
            () => "a", (_, _, _) => throw new InvalidOperationException(), new CancellationToken(true)));
    }

    [Fact]
    public async Task Compare_DifferentOrClearedSelectionIgnoresLateSuccess()
    {
        foreach (var next in new string?[] { "b", null })
        {
            string? current = "a";
            var scanning = new TaskCompletionSource<(CheckpointInfo, CheckpointDifference[])>(TaskCreationOptions.RunContinuationsAsynchronously);
            var operation = CollectorWindow.Compare(s_before, () => { }, (_, _) => scanning.Task,
                () => current, (_, _, _) => throw new InvalidOperationException(), TestContext.Current.CancellationToken);
            current = next;
            scanning.SetResult((s_before, [s_added]));
            await operation;
        }
    }

    [Fact]
    public void SelectedChanges_ReturnsOnlyAddedAndUpdatedRowsInSelectionOrder()
    {
        object?[] selection = [s_updated, null, s_missing, "header", s_unknown, s_added];
        Assert.Equal(new[] { s_updated, s_added }, CollectorWindow.SelectedChanges(selection));
    }

    [Fact]
    public void SelectedChanges_ReturnsEmptyForMissingOrNonDownloadableSelection()
    {
        Assert.Empty(CollectorWindow.SelectedChanges(null));
        Assert.Empty(CollectorWindow.SelectedChanges(Array.Empty<object>()));
        Assert.Empty(CollectorWindow.SelectedChanges(new object[] { s_missing, s_unknown }));
    }
}
