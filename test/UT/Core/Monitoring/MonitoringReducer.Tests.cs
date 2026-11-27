using ShareFileWatch;
using Xunit;

public sealed class MonitoringReducerTests
{
    [Fact]
    public void Apply_ReportsUpdatesAndReappearanceButNotDuplicateOrLostFiles()
    {
        var time = DateTimeOffset.UtcNow;
        string path = Path.GetFullPath("sample.txt");
        var target = new WatchItem { Path = path };
        var state = new AppState { Items = [target], RenotifyUnread = false, NotifyDeleted = true };
        var first = new Fingerprint(time.AddSeconds(-5).ToFileTime(), 1, 10);
        var second = first with { Bytes = 11 };
        var third = first with { Bytes = 12 };

        var initial = Apply(new(ProbeState.Found, first));
        Require(!initial.UnreadChanged && initial.NeedsSave && initial.Alerts.Count == 0 && initial.FileUpdates.Count == 0,
            "初回照会は基準だけを保存する");
        var changed = Apply(new(ProbeState.Found, second));
        Require(changed.UnreadChanged && changed.NeedsSave && changed.Alerts.Single().Path == path &&
            changed.FileUpdates.Single().Expected == second && state.History.Count == 1, "変更を未読・履歴・フック候補に振り分ける");
        var changedAgain = Apply(new(ProbeState.Found, third));
        Require(changedAgain.Alerts.Count == 0 && changedAgain.FileUpdates.Count == 1 && state.History.Count == 2,
            "再通知の抑止はフックに影響しない");
        var unavailable = Apply(new(ProbeState.Unavailable, Error: "接続失敗"));
        Require(!unavailable.UnreadChanged && unavailable.FileUpdates.Count == 0 &&
            target.Baseline == ProbeState.Found && state.History.Count == 2, "通信失敗を削除に変換しない");
        var stale = new WatchObservation(target.Id, target.Revision + 1, new(ProbeState.Missing), null, time, null);
        Require(MonitoringReducer.Apply(state, stale, new(StringComparer.OrdinalIgnoreCase), true) is null &&
            target.Baseline == ProbeState.Found, "別の世代の結果を破棄する");
        var removed = Apply(new(ProbeState.Missing));
        Require(removed.FileUpdates.Count == 0 && state.History.Last().Kind == "消失", "消失ではフックを起動しない");
        var reappeared = Apply(new(ProbeState.Found, first));
        Require(reappeared.FileUpdates.Single().Expected == first && state.History.Last().Kind == "出現",
            "個別ファイルの再出現をフックに渡す");
        Require(Apply(new(ProbeState.Found, first)).FileUpdates.Count == 0, "同じ状態の再照会では再実行しない");

        ObservationOutcome Apply(ProbeResult file) => MonitoringReducer.Apply(state,
            new(target.Id, target.Revision, file, null, time, null), new(StringComparer.OrdinalIgnoreCase), true)!;
    }

    [Fact]
    public void Apply_UnregisteredObservationIsIgnoredWithoutConsumingHistoryDeduplication()
    {
        var state = new AppState();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var observation = new WatchObservation(Guid.NewGuid(), 0, new(ProbeState.Missing),
            null, DateTimeOffset.UnixEpoch, null);
        Assert.Null(MonitoringReducer.Apply(state, observation, seen, true));
        Assert.Empty(state.History);
        Assert.Empty(seen);
    }

    [Fact]
    public void Apply_FolderAddsOnlyNewPresentFilesToHookCandidatesAndDoesNotDoubleReport()
    {
        string root = Path.GetFullPath("monitored");
        string added = Path.Combine(root, "created.txt");
        var target = new WatchItem { Path = root, IsFolder = true, Recursive = true };
        var state = new AppState { Items = [target], NotifyDeleted = true, RenotifyUnread = true };
        var time = DateTimeOffset.UnixEpoch.AddDays(1);
        var original = new Fingerprint(time.AddSeconds(-2).ToFileTime(), 1, 10);
        var later = original with { Bytes = 21 };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Assert.Empty(Apply(new() { ["removed.txt"] = original }).Alerts);
        seen.Add(added.ToUpperInvariant()); // Another registration observed this same entry in the same sweep.
        var result = Apply(new() { ["created.txt"] = later });
        var updated = Assert.Single(result.FileUpdates);
        Assert.Equal(added, updated.Path);
        Assert.Equal(later, updated.Expected);
        Assert.DoesNotContain(result.Alerts, a => a.Path == added);
        Assert.DoesNotContain(state.History, h => h.Path == added);
        Assert.Contains(state.History, h => h.Path == Path.Combine(root, "removed.txt"));

        ObservationOutcome Apply(Dictionary<string, Fingerprint> entries) => MonitoringReducer.Apply(state,
            new(target.Id, target.Revision, null, new FolderResult(entries), time, null), seen, true)!;
    }


    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public void Apply_RenotifyOptionControlsAlertsButNeverDropsHistory(bool renotify, int expectedAlerts)
    {
        var target = new WatchItem { Path = Path.GetFullPath("sample.txt"), Baseline = ProbeState.Found,
            Fingerprint = new(5, 2, 10), Unread = 1 };
        var state = new AppState { Items = [target], RenotifyUnread = renotify };
        var at = DateTimeOffset.UnixEpoch.AddDays(1);
        var observation = new WatchObservation(target.Id, 0, new(ProbeState.Found, new(7, 2, 11)), null, at, null);
        var result = Assert.IsType<ObservationOutcome>(MonitoringReducer.Apply(state, observation, [], false));

        Assert.True(result.UnreadChanged);
        Assert.True(result.NeedsSave);
        Assert.Equal(expectedAlerts, result.Alerts.Count);
        Assert.Empty(result.FileUpdates);
        Assert.Equal(2, target.Unread);
        var history = Assert.Single(state.History);
        Assert.Equal((at, target.Path, "更新"), (history.At, history.Path, history.Kind));
    }

    [Fact]
    public void Apply_FolderTimestampOnlyChangeDoesNotCreateFileHook()
    {
        string root = Path.GetFullPath("monitored");
        var at = DateTimeOffset.UnixEpoch.AddDays(1);
        var target = new WatchItem { Path = root, IsFolder = true, DirectoryStamp = at.AddSeconds(-2).ToFileTime(),
            Baseline = ProbeState.Found, Inventory = new() };
        var state = new AppState { Items = [target], RenotifyUnread = true };
        var folder = new FolderResult(new()) with { DirectoryStamp = at.AddSeconds(-1).ToFileTime() };
        var result = Assert.IsType<ObservationOutcome>(MonitoringReducer.Apply(
            state, new(target.Id, 0, null, folder, at, null), [], true));

        Assert.True(result.UnreadChanged);
        Assert.Empty(result.FileUpdates);
        Assert.Equal(root, Assert.Single(result.Alerts).Path);
        Assert.Equal(root, Assert.Single(state.History).Path);
        Assert.Contains(".", target.PendingFiles.Keys);
    }

    [Fact]
    public void Apply_CapsHistoryAndPreservesNewestUpdate()
    {
        var at = DateTimeOffset.UnixEpoch.AddDays(1);
        var target = new WatchItem { Path = Path.GetFullPath("new.txt"), Baseline = ProbeState.Found,
            Fingerprint = new(5, 2, 10) };
        var state = new AppState { Items = [target],
            History = new(Enumerable.Range(0, AppDefaults.HistoryLimit).Select(i => new HistoryEntry(at, $"old-{i}.txt", "更新"))) };
        MonitoringReducer.Apply(state, new(target.Id, 0, new(ProbeState.Found, new(7, 2, 11)), null, at, null), [], false);

        Assert.Equal(AppDefaults.HistoryLimit, state.History.Count);
        Assert.Equal("old-1.txt", state.History.Peek().Path);
        Assert.Equal(target.Path, state.History.Last().Path);
    }



    [Fact]
    public void Apply_IdenticalFolderScopeDoesNotRewriteTheInventoryButNewScopeDoes()
    {
        var at = DateTimeOffset.UnixEpoch.AddDays(1);
        var root = Path.GetFullPath("scope-test");
        var original = new Dictionary<string, Fingerprint>(StringComparer.OrdinalIgnoreCase)
            { ["report.log"] = new(at.AddSeconds(-1).ToFileTime(), 1, 17) };
        var target = new WatchItem { Path = root, IsFolder = true };
        var state = new AppState { Items = [target] };

        var first = Apply(original, ["*.tmp", "~$*"], at);
        Assert.True(first.NeedsSave);
        Assert.False(first.UnreadChanged);
        Assert.Same(original, target.Inventory);
        var copy = new Dictionary<string, Fingerprint>(original, StringComparer.OrdinalIgnoreCase);

        var unchanged = Apply(copy, ["~$*", "*.TMP"], at.AddSeconds(1));
        Assert.False(unchanged.NeedsSave);
        Assert.Same(original, target.Inventory);
        Assert.Equal(at.AddSeconds(1), target.LastChecked);

        var scopeChanged = Apply(copy, ["*.tmp"], at.AddSeconds(2));
        Assert.True(scopeChanged.NeedsSave);
        Assert.False(scopeChanged.UnreadChanged);
        Assert.Same(copy, target.Inventory);
        Assert.Equal(new[] { "*.tmp" }, target.InventoryExclusions);
        Assert.Empty(state.History);
        Assert.Empty(scopeChanged.Alerts);

        ObservationOutcome Apply(Dictionary<string, Fingerprint> files, string[] exclusions, DateTimeOffset when) =>
            Assert.IsType<ObservationOutcome>(MonitoringReducer.Apply(state,
                new(target.Id, target.Revision, null, new FolderResult(files) { Exclusions = exclusions }, when, null), [], true));
    }

    [Fact]
    public void Apply_UnavailableFolderDoesNotRewriteItsLastCompleteBaseline()
    {
        var at = DateTimeOffset.UnixEpoch.AddDays(1);
        var files = new Dictionary<string, Fingerprint>(StringComparer.OrdinalIgnoreCase)
            { ["report.log"] = new(at.AddSeconds(-1).ToFileTime(), 1, 17) };
        var root = Path.GetFullPath("transient-failure-test");
        var target = new WatchItem { Path = root, IsFolder = true, Baseline = ProbeState.Found,
            Inventory = files, DirectoryStamp = 10, LatestFileWriteAt = at.AddSeconds(-1), InventoryExclusions = ["*.tmp"] };
        var state = new AppState { Items = [target] };
        var unavailable = new WatchObservation(target.Id, target.Revision, null, new FolderResult(null, "share unavailable"), at, null);

        var outcome = Assert.IsType<ObservationOutcome>(MonitoringReducer.Apply(state, unavailable, [], true));
        Assert.False(outcome.NeedsSave);
        Assert.False(outcome.UnreadChanged);
        Assert.Empty(outcome.Alerts);
        Assert.Empty(outcome.FileUpdates);
        Assert.Equal("share unavailable", target.Error);
        Assert.Equal(ProbeState.Found, target.Baseline);
        Assert.Equal(10, target.DirectoryStamp);
        Assert.Same(files, target.Inventory);
        Assert.Empty(state.History);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
