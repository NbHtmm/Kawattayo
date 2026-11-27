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
}
