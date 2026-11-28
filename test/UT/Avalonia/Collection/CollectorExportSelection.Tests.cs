using ShareFileWatch;
using Xunit;

public sealed class CollectorExportSelectionTests
{
    private static readonly CheckpointInfo s_preview = new("initial", "試験前", @"C:\source",
        true, [], DateTimeOffset.UnixEpoch);

    [Fact]
    public async Task ResolveDestination_AcceptsFolderForSamePreview()
    {
        string exactPath = @"C:\export with spaces";
        var output = await CollectorExportSelection.ResolveDestination(s_preview, 2,
            () => s_preview, () => Task.FromResult<string?>(exactPath), TestContext.Current.CancellationToken);

        Assert.Equal(exactPath, output);
    }

    [Fact]
    public async Task ResolveDestination_NoSelectedFilesDoesNotOpenPicker()
    {
        var output = await CollectorExportSelection.ResolveDestination(s_preview, 0,
            () => throw new InvalidOperationException(), () => throw new InvalidOperationException(), TestContext.Current.CancellationToken);
        Assert.Null(output);
    }

    [Fact]
    public async Task ResolveDestination_PickerCancellationDoesNotReplacePreview()
    {
        CheckpointInfo? preview = s_preview;
        Assert.Null(await CollectorExportSelection.ResolveDestination(s_preview, 1,
            () => preview, () => Task.FromResult<string?>(null), TestContext.Current.CancellationToken));
        Assert.Same(s_preview, preview);
    }

    [Fact]
    public async Task ResolveDestination_BlankIsNotAValidDestination()
    {
        Assert.Null(await CollectorExportSelection.ResolveDestination(s_preview, 1,
            () => s_preview, () => Task.FromResult<string?>("  "), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ResolveDestination_NewComparisonWithSameIdDiscardsOldSelection()
    {
        var picker = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        CheckpointInfo? preview = s_preview;
        var resolving = CollectorExportSelection.ResolveDestination(s_preview, 1, () => preview, () => picker.Task, TestContext.Current.CancellationToken);

        preview = s_preview with { };
        picker.SetResult(@"C:\export");
        Assert.Null(await resolving);
    }

    [Fact]
    public async Task ResolveDestination_ClosedCollectorWithLatePickerResultCannotExport()
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var picker = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resolving = CollectorExportSelection.ResolveDestination(s_preview, 1, () => s_preview, () => picker.Task, stop.Token);

        stop.Cancel();
        picker.SetResult(@"C:\export");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resolving);
    }

    [Fact]
    public async Task ResolveDestination_AlreadyCanceledDoesNotOpenPicker()
    {
        var canceled = new CancellationToken(true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CollectorExportSelection.ResolveDestination(
            s_preview, 1, () => s_preview, () => throw new InvalidOperationException(), canceled));
    }

    [Fact]
    public async Task ResolveDestination_RemovedPreviewDiscardsOldSelection()
    {
        Assert.Null(await CollectorExportSelection.ResolveDestination(s_preview, 1,
            () => null, () => Task.FromResult<string?>(@"C:\export"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ResolveDestination_PickerFailureIsNotMistakenForCancellation()
    {
        var failure = new IOException("cannot open folder picker");
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => CollectorExportSelection.ResolveDestination(
            s_preview, 1, () => s_preview, () => Task.FromException<string?>(failure), TestContext.Current.CancellationToken)));
    }
}
