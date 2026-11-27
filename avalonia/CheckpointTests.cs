#if KAWATTAYO_SMOKE
namespace ShareFileWatch;

internal static class CheckpointTests
{
    internal static void Run(string fixture)
    {
        var workspace = Directory.CreateDirectory(Path.Combine(fixture, "checkpoint-" + Guid.NewGuid().ToString("N"))).FullName;
        var source = Directory.CreateDirectory(Path.Combine(workspace, "source")).FullName;
        var output = Directory.CreateDirectory(Path.Combine(workspace, "output")).FullName;
        Directory.CreateDirectory(Path.Combine(source, "nested"));
        var updated = Path.Combine(source, "nested", "計測 & 01.csv");
        File.WriteAllText(updated, "before");
        File.WriteAllText(Path.Combine(source, "deleted.txt"), "before");
        var store = new Checkpoints(workspace);
        var point = store.Create("試験前", source, true, "~$*", default);
        File.WriteAllText(updated, "updated-longer");
        File.Delete(Path.Combine(source, "deleted.txt"));
        File.WriteAllText(Path.Combine(source, "added.txt"), "new");
        File.WriteAllText(Path.Combine(source, "~$temp.docx"), "excluded");
        var result = new Checkpoints(workspace).Compare(point.Id, default);
        Require(result.Changes.Length == 3 && result.Changes.Select(x => x.Kind).Distinct().Count() == 3, "追加・更新・削除・除外");
        File.WriteAllText(Path.Combine(source, "added.txt"), "changed after preview");
        var copies = Checkpoints.Collect(point, result.Changes, output, default);
        Require(copies.Length == 2 && copies.Count(x => x.Saved is not null) == 1 &&
            File.ReadAllText(Path.Combine(output, "nested", "計測 & 01.csv")) == "updated-longer", "保存階層・古いプレビュー");
        var repeated = new Checkpoints(workspace).Compare(point.Id, default);
        Require(repeated.Changes.Length == 3 && store.List().Length == 1, "基準を進めない");
        var again = Checkpoints.Collect(point, repeated.Changes, output, default);
        Require(again.Count(x => x.Saved is not null) == 1, "既存ファイルは上書きしない");
        Directory.Move(source, source + "-offline");
        var offline = new Checkpoints(workspace).Compare(point.Id, default).Changes;
        Require(offline.Length == 1 && offline[0].Kind == DifferenceKind.Unconfirmed, "ルート消失を各ファイルの削除にしない");
    }
    private static void Require(bool condition, string test)
    {
        if (!condition) throw new InvalidOperationException("差分収集テスト: " + test);
    }
}
#endif
