using System.Text.Json;
using Reqnroll;
using Xunit;
using static Kawattayo.Sct.Steps.ProductCli;

namespace Kawattayo.Sct.Steps;

[Binding]
public sealed class CheckpointSafetySteps
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Work, "監視元")).FullName;
    private readonly string _destination = Directory.CreateDirectory(Path.Combine(Work, "収集先")).FullName;
    private string _id = "";
    private string _otherId = "";
    private CliResult? _diff;
    private CliResult? _copy;
    private CliResult[] _responses = [];

    private const string Active = "active.txt";
    private const string Removed = "removed.txt";
    private const string Added = "added.txt";
    private const string NewContent = "updated content with a different size";
    private const string Sentinel = "do not overwrite this evidence";

    [Given("^既存の文書を含むフォルダを基準にした$")]
    public async Task ExistingBaseline()
    {
        Write(Active, "old");
        _id = await Create("baseline", "create");
    }

    [When("^監視先を一時的に別名へ移して差分を取り、収集する$")]
    public async Task MoveAway()
    {
        Directory.Move(_root, _root + ".offline");
        _diff = await Diff(_id, "offline-diff");
        _copy = await Collect(_destination, "offline-collect");
    }

    [Then("^削除ではなく確認不能となり、保存先には何も書かれない$")]
    public void AssertUnavailableNotDeleted()
    {
        var diff = Assert.IsType<CliResult>(_diff);
        Assert.Equal(3, diff.ExitCode);
        using var diffJson = JsonDocument.Parse(diff.Output);
        var entry = Assert.Single(diffJson.RootElement.EnumerateArray());
        Assert.Equal("unconfirmed", entry.GetProperty("kind").GetString());
        Assert.False(string.IsNullOrWhiteSpace(entry.GetProperty("error").GetString()));

        var copy = Assert.IsType<CliResult>(_copy);
        Assert.Equal(3, copy.ExitCode);
        using var copyJson = JsonDocument.Parse(copy.Output);
        Assert.Empty(copyJson.RootElement.GetProperty("files").EnumerateArray());
        Assert.Single(copyJson.RootElement.GetProperty("unconfirmed").EnumerateArray());
        Assert.Empty(Directory.EnumerateFileSystemEntries(_destination));
        Assert.Equal("old", File.ReadAllText(Path.Combine(_root + ".offline", Active)));
    }

    [When("^監視先を戻して元の文書を更新する$")]
    public async Task RestoreAndUpdate()
    {
        Directory.Move(_root + ".offline", _root);
        Write(Active, NewContent);
        _diff = await Diff(_id, "recovered-diff");
    }

    [Then("^同じ基準から元の文書の更新だけが見える$")]
    public void AssertRecovery() => AssertChanges(_diff, (Active, "updated"));

    [Given("^更新予定と削除予定の文書を基準にした$")]
    public async Task TwoFileBaseline()
    {
        Write(Active, "old");
        Write(Removed, "to delete");
        _id = await Create("baseline", "create");
    }

    [When("^文書を追加・更新・削除し、保存先に同名の成果物を置く$")]
    public async Task MixChanges()
    {
        Write(Active, NewContent);
        Write(Added, "new");
        File.Delete(Path.Combine(_root, Removed));
        File.WriteAllText(Path.Combine(_destination, Active), Sentinel);
        _diff = await Diff(_id, "mixed-diff");
    }

    [Then("^差分は追加・更新・削除に分かれる$")]
    public void AssertMixedDiff() => AssertChanges(_diff, (Active, "updated"), (Added, "added"), (Removed, "deleted"));

    [When("^差分を収集する$")]
    public async Task CollectMixed() => _copy = await Collect(_destination, "mixed-collect");

    [Then("^保存先の既存文書を保護し、追加文書だけが保存される$")]
    public void AssertPartialSave()
    {
        var copy = Assert.IsType<CliResult>(_copy);
        Assert.Equal(3, copy.ExitCode);
        using var doc = JsonDocument.Parse(copy.Output);
        var rows = doc.RootElement.GetProperty("files").EnumerateArray().ToArray();
        Assert.Equal(2, rows.Length);
        Assert.Single(rows, entry => entry.GetProperty("saved").GetString() == Path.Combine(_destination, Added));
        Assert.Single(rows, entry => entry.GetProperty("saved").ValueKind == JsonValueKind.Null &&
            !string.IsNullOrWhiteSpace(entry.GetProperty("error").GetString()));
        Assert.Empty(doc.RootElement.GetProperty("unconfirmed").EnumerateArray());
        Assert.Equal(Sentinel, File.ReadAllText(Path.Combine(_destination, Active)));
        Assert.Equal("new", File.ReadAllText(Path.Combine(_destination, Added)));
        Assert.False(File.Exists(Path.Combine(_destination, Removed)));
        Assert.Equal(NewContent, File.ReadAllText(Path.Combine(_root, Active)));
    }

    [Given("^再帰オフでOfficeの一時ファイルとtmpファイルを除外した基準がある$")]
    public async Task ScopedBaseline() =>
        _id = await Create("scoped", "create", "--no-recursive", "--exclude", "~$*", "--exclude", "*.tmp");

    [When("^直下・下位フォルダ・除外対象に文書を追加する$")]
    public async Task PopulateScope()
    {
        Directory.CreateDirectory(Path.Combine(_root, "nested"));
        Write(Path.Combine("nested", "hidden.txt"), "nested");
        Write("~$proposal.docx", "office temporary");
        Write("scratch.TMP", "temporary");
        Write(Added, "new");
        _diff = await Diff(_id, "scoped-diff");
    }

    [Then("^直下の除外されていない文書だけが差分に出る$")]
    public void AssertScope() => AssertChanges(_diff, (Added, "added"));

    [Given("^同じ名前で2件の基準を作った$")]
    public async Task DuplicateNames()
    {
        _id = await Create("same-name", "create-first");
        _otherId = await Create("same-name", "create-second");
        Assert.NotEqual(_id, _otherId);
    }

    [When("^一覧・同名指定・存在しないID・不足引数・ID指定で問い合わせる$")]
    public async Task QueryOptions()
    {
        _responses =
        [
            await RunAsync("list", "checkpoint", "list"),
            await Diff("same-name", "ambiguous"),
            await Diff("not-a-checkpoint", "missing"),
            await RunAsync("invalid", "diff", "--checkpoint"),
            await Diff(_id, "id"),
        ];
    }

    [Then("^一覧は両ID、同名と不足引数は使用法エラー、存在しないIDは処理エラー、正しいIDは空差分を返す$")]
    public void AssertCliContract()
    {
        Assert.Equal(new[] { 0, 2, 4, 2, 0 }, _responses.Select(result => result.ExitCode));
        var ids = _responses[0].Output.Split('\n').Select(line => line.Split('\t')[0]);
        Assert.Equal(new[] { _id, _otherId }.Order(), ids.Order());
        Assert.All(_responses.Skip(1).Take(3), response =>
        {
            Assert.Empty(response.Output);
            Assert.NotEmpty(response.Error);
        });
        AssertChanges(_responses[4]);
    }

    [When("^文書を更新し、監視先とその配下を保存先に指定する$")]
    public async Task TrySavingInside()
    {
        Write(Active, NewContent);
        _responses = [await Collect(_root, "same-root"), await Collect(Path.Combine(_root, "output"), "inside-root")];
    }

    [Then("^どちらも拒否し、監視先に出力を作らない$")]
    public void AssertNoWritesInside()
    {
        Assert.All(_responses, response =>
        {
            Assert.Equal(4, response.ExitCode);
            Assert.NotEmpty(response.Error);
        });
        Assert.Equal(Active, Path.GetFileName(Assert.Single(Directory.EnumerateFileSystemEntries(_root))));
        Assert.Equal(NewContent, File.ReadAllText(Path.Combine(_root, Active)));
    }

    [Given("^基準がない試験用の保存先がある$")]
    public async Task EmptyCatalog()
    {
        var result = await RunAsync("initial-list", "checkpoint", "list");
        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Output);
    }

    [When("^存在しない監視フォルダで基準を作る$")]
    public async Task TryMissingRoot() =>
        _responses = [await RunAsync("create-missing", "checkpoint", "create",
            "--name", "absent", "--root", Path.Combine(_root, "nonexistent"))];

    [Then("^基準作成に失敗し、一覧は空のままである$")]
    public async Task AssertNoFailedCheckpoint()
    {
        var rejected = Assert.Single(_responses);
        Assert.Equal(4, rejected.ExitCode);
        Assert.NotEmpty(rejected.Error);
        Assert.Empty(rejected.Output);
        var result = await RunAsync("after-failed-create", "checkpoint", "list");
        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Output);
    }

    [When("^長さの境界、相対パス、重複引数で基準を作る$")]
    public async Task TryNameAndArgumentBoundaries()
    {
        _id = await Create("a", "minimum-name");
        _otherId = await Create(new string('b', 100), "maximum-name");
        _responses =
        [
            await RunAsync("blank-name", "checkpoint", "create", "--name", " ", "--root", _root),
            await RunAsync("long-name", "checkpoint", "create", "--name", new string('c', 101), "--root", _root),
            await RunAsync("relative-root", "checkpoint", "create", "--name", "relative", "--root", "folder"),
            await RunAsync("duplicate-name-arg", "checkpoint", "create", "--name", "first", "--name", "second", "--root", _root),
        ];
    }

    [Then("^許容範囲の名前だけが登録される$")]
    public async Task AssertOnlyValidNames()
    {
        Assert.All(_responses, response =>
        {
            Assert.Equal(2, response.ExitCode);
            Assert.Empty(response.Output);
            Assert.NotEmpty(response.Error);
        });
        var result = await RunAsync("final-list", "checkpoint", "list");
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(new[] { _id, _otherId }.Order(),
            result.Output.Split('\n').Select(line => line.Split('\t')[0]).Order());
    }

    private void Write(string name, string contents) => File.WriteAllText(Path.Combine(_root, name), contents);

    private async Task<string> Create(string name, string label, params string[] options)
    {
        var result = await RunAsync(label, ["checkpoint", "create", "--name", name, "--root", _root, .. options]);
        Assert.Equal(0, result.ExitCode);
        Assert.Matches("^[a-f0-9]{32}$", result.Output);
        return result.Output;
    }

    private static Task<CliResult> Diff(string id, string label) =>
        RunAsync(label, "diff", "--checkpoint", id, "--json");

    private Task<CliResult> Collect(string destination, string label) =>
        RunAsync(label, "collect", "--checkpoint", _id, "--output", destination, "--json");

    private static void AssertChanges(CliResult? candidate, params (string? Path, string? Kind)[] expected)
    {
        var result = Assert.IsType<CliResult>(candidate);
        Assert.Equal(0, result.ExitCode);
        using var doc = JsonDocument.Parse(result.Output);
        Assert.Equal(expected.OrderBy(item => item.Path), doc.RootElement.EnumerateArray().Select(item =>
            (item.GetProperty("path").GetString(), item.GetProperty("kind").GetString())).OrderBy(item => item.Item1));
    }
}
