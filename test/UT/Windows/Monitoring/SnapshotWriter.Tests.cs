using System.Collections.Concurrent;
using ShareFileWatch;
using Xunit;

public sealed class SnapshotWriterTests
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task FlushAsync_CoalescesQueuedRequestsAndWaitsUntilLatestSnapshotIsSaved()
    {
        var cancellation = TestContext.Current.CancellationToken;
        var state = new AppState { PollSeconds = 10 };
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var writes = new ConcurrentQueue<int>();
        int active = 0;
        bool overlap = false;
        int callbacks = 0;

        var writer = new SnapshotWriter(state.CaptureForSave, snapshot =>
        {
            if (Interlocked.Increment(ref active) != 1) overlap = true;
            try
            {
                if (snapshot.PollSeconds == 10)
                {
                    started.TrySetResult();
                    if (!release.Wait(s_timeout, cancellation)) throw new TimeoutException("First write was not released.");
                }

                writes.Enqueue(snapshot.PollSeconds);
            }
            finally { Interlocked.Decrement(ref active); }
        }, () => Interlocked.Increment(ref callbacks));

        await writer.FlushAsync().WaitAsync(s_timeout, cancellation);
        Assert.Empty(writes);

        writer.Enqueue();
        Task? flush = null;
        try
        {
            await started.Task.WaitAsync(s_timeout, cancellation);
            state.PollSeconds = 20;
            writer.Enqueue();
            state.PollSeconds = 30;
            writer.Enqueue();
            flush = writer.FlushAsync();
            Assert.False(flush.IsCompleted);
        }
        finally { release.Set(); }

        await flush.WaitAsync(s_timeout, cancellation);
        Assert.Equal(new[] { 10, 30 }, writes.ToArray());
        Assert.False(overlap);
        Assert.Equal(2, callbacks);
        Assert.Null(writer.Error);
    }

    [Fact]
    public async Task FlushAsync_RetriesOnceAndReportsFailureUntilLatestVersionCanBeSaved()
    {
        var cancellation = TestContext.Current.CancellationToken;
        var state = new AppState { PollSeconds = 15 };
        var writes = new ConcurrentQueue<int>();
        int attempts = 0;
        var writer = new SnapshotWriter(state.CaptureForSave, snapshot =>
        {
            if (Interlocked.Increment(ref attempts) <= 2) throw new UnauthorizedAccessException("fixture access denied");
            writes.Enqueue(snapshot.PollSeconds);
        }, () => { });

        writer.Enqueue();
        var error = await Assert.ThrowsAsync<IOException>(() => writer.FlushAsync().WaitAsync(s_timeout, cancellation));
        Assert.Equal("fixture access denied", error.Message);
        Assert.Equal(error.Message, writer.Error);
        Assert.Equal(2, attempts);
        Assert.Empty(writes);

        state.PollSeconds = 45;
        writer.Enqueue();
        await writer.FlushAsync().WaitAsync(s_timeout, cancellation);
        Assert.Equal(45, Assert.Single(writes));
        Assert.Equal(3, attempts);
        Assert.Null(writer.Error);
    }

    [Fact]
    public async Task FlushAsync_AFirstFailureDoesNotDiscardANewerQueuedSnapshot()
    {
        var cancellation = TestContext.Current.CancellationToken;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var finishFirst = new ManualResetEventSlim();
        var attempts = new ConcurrentQueue<int>();
        var statesAfterWrites = new ConcurrentQueue<string?>();
        var state = new AppState { PollSeconds = 10 };
        SnapshotWriter writer = null!;
        writer = new SnapshotWriter(state.CaptureForSave, snapshot =>
        {
            attempts.Enqueue(snapshot.PollSeconds);
            if (snapshot.PollSeconds != 10) return;

            started.TrySetResult();
            if (!finishFirst.Wait(s_timeout, cancellation)) throw new TimeoutException("First write was not released.");
            throw new IOException("fixture transient write error");
        }, () => statesAfterWrites.Enqueue(writer.Error));

        writer.Enqueue();
        try
        {
            await started.Task.WaitAsync(s_timeout, cancellation);
            state.PollSeconds = 25;
            writer.Enqueue();
        }
        finally { finishFirst.Set(); }

        await writer.FlushAsync().WaitAsync(s_timeout, cancellation);
        await writer.FlushAsync().WaitAsync(s_timeout, cancellation);
        Assert.Equal(new[] { 10, 25 }, attempts.ToArray());
        Assert.Equal(new string?[] { "fixture transient write error", null }, statesAfterWrites.ToArray());
    }

    [Fact]
    public async Task FlushAsync_RetriesTheSameSnapshotFollowingOneTransientFailure()
    {
        var cancellation = TestContext.Current.CancellationToken;
        int attempts = 0;
        int callbacks = 0;
        var writer = new SnapshotWriter(() => new AppState(), _ =>
        {
            if (Interlocked.Increment(ref attempts) == 1) throw new IOException("fixture transient failure");
        }, () => Interlocked.Increment(ref callbacks));

        writer.Enqueue();
        await writer.FlushAsync().WaitAsync(s_timeout, cancellation);

        Assert.Equal(2, attempts);
        Assert.Equal(2, callbacks);
        Assert.Null(writer.Error);
    }

    [Fact]
    public async Task FlushAsync_DoesNotMistakeAnUnexpectedErrorForAnExpectedDiskFailure()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var release = new ManualResetEventSlim();
        int callbacks = 0;
        var writer = new SnapshotWriter(() => new AppState(), _ =>
        {
            if (!release.Wait(s_timeout, cancellation)) throw new TimeoutException("Write was not released.");
            throw new InvalidOperationException("fixture invariant");
        }, () => Interlocked.Increment(ref callbacks));

        writer.Enqueue();
        Task flush = writer.FlushAsync();
        release.Set();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => flush.WaitAsync(s_timeout, cancellation));

        Assert.Equal("fixture invariant", ex.Message);
        Assert.Null(writer.Error);
        Assert.Equal(0, callbacks);
    }

}
