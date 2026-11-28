using ShareFileWatch;
using Xunit;

public sealed class MonitoringCacheTests
{
    [Theory]
    [InlineData("report.txt", true)]
    [InlineData(@"run\report.txt", true)]
    [InlineData("run/report.txt", true)]
    [InlineData("run/集計 01.txt", true)]
    [InlineData("", false)]
    [InlineData(".", false)]
    [InlineData("..", false)]
    [InlineData(@"run\..\report.txt", false)]
    [InlineData(@"C:\report.txt", false)]
    [InlineData(@"C:report.txt", false)]
    [InlineData(@"\report.txt", false)]
    [InlineData(@"run\\report.txt", false)]
    [InlineData(@"run\report.txt\", false)]
    [InlineData("run/report?.txt", false)]
    public void IsRelativeEntry_AllowsOnlyUnambiguousRelativeFileNames(string path, bool expected)
    {
        Assert.Equal(expected, MonitoringCache.IsRelativeEntry(path));
    }

    [Fact]
    public void ValidateObservations_AcceptsFolderMarkerAndIndependentPendingFile()
    {
        var watch = new WatchItem { IsFolder = true, Unread = 2 };
        watch.Inventory = new() { ["report.txt"] = new(12, 5, 34) };
        watch.PendingFiles["."] = new(".", "更新", DateTimeOffset.UnixEpoch);
        watch.PendingFiles["report.txt"] = new("report.txt", "追加", DateTimeOffset.UnixEpoch);

        MonitoringCache.ValidateObservations(watch);

        Assert.Equal(2, watch.Unread);
        Assert.Equal(2, watch.PendingFiles.Count);
        Assert.Equal((ulong)34, watch.Inventory["report.txt"].Bytes);
    }

    [Fact]
    public void ValidateObservations_RejectsFolderMarkerForAFileTarget()
    {
        var watch = new WatchItem { IsFolder = false, Unread = 1 };
        watch.PendingFiles["."] = new(".", "更新", DateTimeOffset.UnixEpoch);

        Assert.Throws<IOException>(() => MonitoringCache.ValidateObservations(watch));
    }

    [Fact]
    public void ValidateObservations_RejectsEveryIndependentInvalidField()
    {
        var negativeUnread = new WatchItem { Unread = -1 };
        var invalidInventory = new WatchItem { IsFolder = true };
        invalidInventory.Inventory = new() { [""] = new(12, 5, 34) };
        var invalidPending = new WatchItem { IsFolder = true, Unread = 1 };
        invalidPending.PendingFiles[""] = new("", "更新", DateTimeOffset.UnixEpoch);

        Assert.Throws<IOException>(() => MonitoringCache.ValidateObservations(negativeUnread));
        Assert.Throws<IOException>(() => MonitoringCache.ValidateObservations(invalidInventory));
        Assert.Throws<IOException>(() => MonitoringCache.ValidateObservations(invalidPending));
    }
    [Fact]
    public void Save_SkipsEmptyAndUnchangedState()
    {
        var writer = new RecordingWriter();
        int opens = 0;
        var cache = new MonitoringCache("unused", () => { opens++; return writer; });
        var state = new AppState();
        cache.Save(state);
        Assert.Equal(0, opens);

        state.Items.Add(CreateTarget());
        cache.Save(state);
        cache.Save(state);
        Assert.Equal(1, opens);
        Assert.Equal(new[] { "BEGIN IMMEDIATE", "schema", "write-target", "COMMIT", "dispose" }, writer.Events);
    }

    [Fact]
    public void Save_ReusesUnchangedInventoryAndPrunesSupersededOrRemovedSnapshots()
    {
        var writer = new RecordingWriter();
        var cache = new MonitoringCache("unused", () => writer);
        var target = CreateTarget();
        target.Inventory = new() { ["old.txt"] = new(12, 5, 34) };
        var state = new AppState { Items = [target] };

        cache.Save(state);
        Assert.Equal("snapshot-1", Assert.Single(writer.Targets).SnapshotId);
        Assert.Equal(new[] { "BEGIN IMMEDIATE", "schema", "snapshot", "write-target", "references", "prune", "COMMIT", "dispose" }, writer.Events);

        writer.Events.Clear();
        target.Unread = 1;
        cache.Save(state);
        Assert.Equal("snapshot-1", writer.Targets[^1].SnapshotId);
        Assert.DoesNotContain("snapshot", writer.Events);
        Assert.DoesNotContain("prune", writer.Events);

        target.Inventory = new() { ["next.txt"] = new(20, 6, 35) };
        cache.Save(state);
        Assert.Equal("snapshot-2", writer.Targets[^1].SnapshotId);
        Assert.Equal(new[] { "snapshot-2" }, writer.LastPrune);

        target.Inventory = null;
        cache.Save(state);
        Assert.Null(writer.Targets[^1].SnapshotId);
        Assert.Empty(writer.LastPrune);

        writer.Events.Clear();
        state.Items.Clear();
        cache.Save(state);
        Assert.Equal(new[] { "BEGIN IMMEDIATE", "schema", "remove-target", "references", "prune", "COMMIT", "dispose" }, writer.Events);
        Assert.Empty(writer.LastPrune);
    }

    [Theory]
    [InlineData("BEGIN IMMEDIATE")]
    [InlineData("schema")]
    [InlineData("remove-target")]
    [InlineData("snapshot")]
    [InlineData("write-target")]
    [InlineData("history")]
    [InlineData("references")]
    [InlineData("prune")]
    [InlineData("COMMIT")]
    public void Save_KeepsPriorBaselineAndRetriesAfterWriteFailure(string failure)
    {
        var baselineWriter = new RecordingWriter();
        var failedWriter = new RecordingWriter { FailOn = failure };
        var retryWriter = new RecordingWriter();
        var writers = new Queue<RecordingWriter>([baselineWriter, failedWriter, retryWriter]);
        var cache = new MonitoringCache("unused", () => writers.Dequeue());
        var removed = CreateTarget();
        var changed = CreateTarget();
        var state = new AppState { Items = [removed, changed] };
        cache.Save(state);

        state.Items.Remove(removed);
        changed.Unread = 1;
        changed.Inventory = new() { ["next.txt"] = new(20, 6, 35) };
        state.History.Enqueue(new(DateTimeOffset.UnixEpoch, changed.Path, "updated"));

        Assert.Throws<IOException>(() => cache.Save(state));
        Assert.Equal("dispose", failedWriter.Events[^1]);
        cache.Save(state);
        Assert.Equal(new[] { removed.Id }, retryWriter.Removed);
        Assert.Equal(0, Assert.Single(retryWriter.Priors)!.Observation.Unread);
        Assert.Equal(1, Assert.Single(retryWriter.Targets).Observation.Unread);
        Assert.Equal(state.History.ToArray(), retryWriter.History);
        Assert.Equal(new[] { "snapshot-1" }, retryWriter.LastPrune);

        cache.Save(state); // Successful retry advances the baseline; no fourth writer may open.
        Assert.Empty(writers);
    }

    [Fact]
    public void Save_PersistsHistoryEvenWithoutAnyTargetChanges()
    {
        var writer = new RecordingWriter();
        var cache = new MonitoringCache("unused", () => writer);
        var state = new AppState();
        state.History.Enqueue(new(DateTimeOffset.UnixEpoch, "report.txt", "updated"));
        cache.Save(state);

        Assert.Equal(new[] { "BEGIN IMMEDIATE", "schema", "history", "COMMIT", "dispose" }, writer.Events);
        Assert.Equal(state.History.ToArray(), writer.History);
    }

    [Fact]
    public void Save_OpeningWriterFailureDoesNotSuppressNextSave()
    {
        var writer = new RecordingWriter();
        bool first = true;
        var cache = new MonitoringCache("unused", () =>
        {
            if (!first) return writer;
            first = false;
            throw new IOException("could not open the database");
        });
        var state = new AppState { Items = [CreateTarget()] };

        Assert.Throws<IOException>(() => cache.Save(state));
        cache.Save(state);
        Assert.Equal(state.Items[0].Id, Assert.Single(writer.Targets).Id);
    }

    private static WatchItem CreateTarget() => new() { Id = Guid.NewGuid(), Path = "report.txt", IsFolder = true };

    private sealed class RecordingWriter : IWatchCacheWriter
    {
        private int snapshotCount;
        private readonly Dictionary<Guid, CachedTarget> snapshots = [];
        public string? FailOn { get; init; }
        public List<string> Events { get; } = [];
        public List<Guid> Removed { get; } = [];
        public List<CachedTarget> Targets { get; } = [];
        public List<CachedTarget?> Priors { get; } = [];
        public HistoryEntry[] History { get; private set; } = [];
        public string[] LastPrune { get; private set; } = [];

        public void Exec(string sql) => Record(sql);
        public void CreateWatchCache() => Record("schema");
        public void RemoveTarget(Guid id) { Record("remove-target"); Removed.Add(id); snapshots.Remove(id); }
        public string Write(Dictionary<string, Fingerprint> files) { Record("snapshot"); return $"snapshot-{++snapshotCount}"; }
        public void WriteTarget(CachedTarget target, CachedTarget? prior)
        {
            Record("write-target");
            Targets.Add(target);
            Priors.Add(prior);
            snapshots[target.Id] = target;
        }
        public void WriteHistory(IEnumerable<HistoryEntry> entries) { Record("history"); History = entries.ToArray(); }
        public List<string> ReferencedSnapshots()
        {
            Record("references");
            return snapshots.Values.Select(t => t.SnapshotId).OfType<string>().ToList();
        }
        public void Prune(IEnumerable<string> keep) { Record("prune"); LastPrune = keep.ToArray(); }
        public void Dispose() => Record("dispose");

        private void Record(string stage)
        {
            Events.Add(stage);
            if (stage == FailOn) throw new IOException(stage);
        }
    }
}
