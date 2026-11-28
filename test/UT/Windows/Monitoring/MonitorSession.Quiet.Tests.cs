using ShareFileWatch;
using Xunit;

public sealed class MonitorSessionQuietTests
{
    [Fact]
    public void Signal_IgnoresAppFilesAndCoalescesCaseVariantsWhileKeepingVersions()
    {
        const string tracked = @"C:\test\report.txt";
        const string owned = @"C:\test\cache.sqlite3";
        var feed = new MonitorSession.ChangeFeed(@"C:\test", recursive: false,
            name => string.Equals(name, owned, StringComparison.OrdinalIgnoreCase));
        Assert.True(feed.Names.TryAdd(tracked, 0));

        feed.Signal(owned.ToUpperInvariant());
        Assert.Equal(0L, feed.Stamp(folder: true, tracked));
        Assert.Empty(feed.Snapshot(0).Paths!);
        feed.Signal(tracked);
        feed.Signal(tracked.ToUpperInvariant());

        Assert.Equal(2L, feed.Stamp(folder: true, tracked));
        Assert.Equal(2L, feed.Stamp(folder: false, tracked));
        Assert.Equal(0L, feed.Stamp(folder: false, owned));
        var snapshot = feed.Snapshot(0);
        Assert.Equal(2L, snapshot.Stamp);
        Assert.Equal(tracked, Assert.Single(snapshot.Paths!), ignoreCase: true);
        Assert.Empty(feed.Snapshot(2).Paths!);
    }

    [Fact]
    public void Snapshot_RequestsFullReconciliationWhenTheSavedCursorIsOlderThanJournal()
    {
        const int limit = 4096;
        var feed = new MonitorSession.ChangeFeed(@"C:\test", recursive: true, _ => false);
        for (int index = 1; index <= limit; index++)
            feed.Signal($@"C:\test\report-{index}.txt");

        var lastWithinCapacity = feed.Snapshot(0);
        Assert.Equal((long)limit, lastWithinCapacity.Stamp);
        Assert.Equal(limit, lastWithinCapacity.Paths!.Length);
        feed.Signal(@"C:\test\latest.txt");

        var behind = feed.Snapshot(limit - 1);
        Assert.Equal(limit + 1L, behind.Stamp);
        Assert.Null(behind.Paths);
        Assert.Equal(@"C:\test\latest.txt", Assert.Single(feed.Snapshot(limit).Paths!));
        Assert.Empty(feed.Snapshot(limit + 1).Paths!);
    }

    [Fact]
    public void Snapshot_AfterConcurrentSignalsShowsEveryDistinctPathAndTheLatestStamp()
    {
        var feed = new MonitorSession.ChangeFeed(@"C:\test", recursive: true, _ => false);
        var names = Enumerable.Range(0, 64).Select(i => $@"C:\test\run-{i}.txt").ToArray();
        Parallel.ForEach(names, feed.Signal);

        var actual = feed.Snapshot(0);

        Assert.Equal((long)names.Length, actual.Stamp);
        Assert.Equal(names.Order(StringComparer.Ordinal), actual.Paths!.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Snapshot_UsesTheLatestEventForEachPathAndAnExclusiveCursor()
    {
        var feed = new MonitorSession.ChangeFeed(@"C:\test", recursive: true, _ => false);
        const string first = @"C:\test\first.txt";
        const string second = @"C:\test\second.txt";
        feed.Signal(first);  // Version 1.
        feed.Signal(second); // Version 2.
        feed.Signal(first);  // Version 3 replaces the older record for first.txt.

        var afterSecond = feed.Snapshot(2);
        Assert.Equal(3L, afterSecond.Stamp);
        Assert.Equal(first, Assert.Single(afterSecond.Paths!));
        var afterFirst = feed.Snapshot(1);
        Assert.Equal(new[] { first, second }, afterFirst.Paths!.Order(StringComparer.Ordinal));
        Assert.Empty(feed.Snapshot(4).Paths!);
    }

    [Fact]
    public void Signal_ConcurrentAliasesIncreaseTheTrackedStampWithoutDuplicatingTheRow()
    {
        const string path = @"C:\test\report.txt";
        const string other = @"C:\test\other.txt";
        var feed = new MonitorSession.ChangeFeed(@"C:\test", recursive: true, _ => false);
        Assert.True(feed.Names.TryAdd(path, 0));
        Parallel.For(0, 64, index => feed.Signal(index % 2 == 0 ? path : path.ToUpperInvariant()));
        feed.Signal(other);

        Assert.Equal(64L, feed.Stamp(folder: false, path.ToUpperInvariant()));
        Assert.Equal(0L, feed.Stamp(folder: false, other));
        Assert.Equal(65L, feed.Stamp(folder: true, other));
        Assert.Equal(2, feed.Snapshot(0).Paths!.Length);
        Assert.False(feed.Names.ContainsKey(other));
    }

    [Fact]
    public async Task Start_LateRetiredPairIsDisposedWithoutTouchingReplacementForTheSameDirectory()
    {
        const string directory = @"C:\test";
        const string file = @"C:\test\report.txt";
        var gate = new TaskCompletionSource<(IDisposable?, IDisposable?)>();
        var oldMain = new Subscription();
        var oldParent = new Subscription();
        var newMain = new Subscription();
        var newParent = new Subscription();
        int oldAttempts = 0;
        var old = new MonitorSession.ChangeFeed(directory, false, _ => false, () =>
        {
            oldAttempts++;
            return gate.Task;
        });
        var replacement = new MonitorSession.ChangeFeed(directory, false, _ => false,
            () => Task.FromResult<(IDisposable?, IDisposable?)>((newMain, newParent)));

        var start = old.Start(0);
        Assert.False(start.IsCompleted);
        Assert.False(old.Available);
        old.Retire();
        await replacement.Start(0);
        replacement.Signal(file);

        gate.SetResult((oldMain, oldParent));
        await start;
        await old.Start(10000);
        old.Retire();

        Assert.Equal(1, oldAttempts);
        Assert.False(old.Available);
        Assert.Equal(0, old.Epoch);
        Assert.Equal(1, oldMain.Disposals);
        Assert.Equal(1, oldParent.Disposals);
        Assert.True(replacement.Available);
        Assert.Equal(1, replacement.Epoch);
        Assert.Equal(0, newMain.Disposals);
        Assert.Equal(0, newParent.Disposals);
        Assert.Equal(file, Assert.Single(replacement.Snapshot(0).Paths!));
        replacement.Retire();
        Assert.False(replacement.Available);
        Assert.Equal(1, newMain.Disposals);
        Assert.Equal(1, newParent.Disposals);
    }

    [Fact]
    public async Task Start_UnavailableAndFailedFeedsRetryAtTheExactDeadline()
    {
        var first = new Subscription();
        var firstParent = new Subscription();
        var second = new Subscription();
        int attempts = 0;
        var feed = new MonitorSession.ChangeFeed(@"C:\test", false, _ => false, () =>
            Task.FromResult<(IDisposable?, IDisposable?)>((++attempts) switch
            {
                1 => (null, null),
                2 => (first, firstParent),
                _ => (second, null)
            }));

        await feed.Start(10);
        Assert.False(feed.Available);
        Assert.Equal(0, feed.Epoch);
        await feed.Start(5009);
        Assert.Equal(1, attempts);
        await feed.Start(5010);
        Assert.True(feed.Available);
        Assert.Equal(1, feed.Epoch);
        await feed.Start(5011);
        Assert.Equal(2, attempts);

        feed.Failed = true;
        await feed.Start(10009);
        Assert.False(feed.Available);
        Assert.Equal(2, feed.Epoch);
        Assert.Equal(1, first.Disposals);
        Assert.Equal(1, firstParent.Disposals);
        Assert.Equal(2, attempts);
        await feed.Start(10010);
        Assert.True(feed.Available);
        Assert.False(feed.Failed);
        Assert.Equal(3, feed.Epoch);
        Assert.Equal(3, attempts);
        feed.Retire();
        Assert.Equal(1, second.Disposals);
    }

    private sealed class Subscription : IDisposable
    {
        internal int Disposals { get; private set; }
        public void Dispose() => Disposals++;
    }
}
