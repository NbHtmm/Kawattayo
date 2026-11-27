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

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
