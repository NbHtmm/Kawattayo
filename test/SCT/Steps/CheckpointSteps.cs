using System.Text.Json;
using Reqnroll;
using Xunit;
using static Kawattayo.Sct.Steps.ProductCli;

namespace Kawattayo.Sct.Steps;

/// <summary>配布したCLIの入出力だけで差分収集を試験する。</summary>
[Binding]
public sealed class CheckpointSteps
{
    private const string FileName = "文書 01.txt";
    private const string Updated = "updated, longer content";
    private const string Existing = "saved evidence from a previous run";
    private readonly string _work = Work;
    private string _root = "";
    private string _destination = "";
    private string _checkpoint = "";
    private CliResult? _diff;

    [Given("^日本語と空白を含む監視先に検証用の文書がある$")]
    public void PrepareDocument()
    {
        _root = Directory.CreateDirectory(Path.Combine(_work, "監視 フォルダ")).FullName;
        _destination = Directory.CreateDirectory(Path.Combine(_work, "保存 フォルダ")).FullName;
        File.WriteAllText(SourcePath, "before");
    }

    [When("^配布CLIでチェックポイントを作る$")]
    public async Task CreateCheckpoint()
    {
        var result = await RunAsync("cli-create", "checkpoint", "create", "--name", "baseline", "--root", _root);
        Assert.Equal(0, result.ExitCode);
        Assert.Matches("^[a-f0-9]{32}$", result.Output);
        _checkpoint = result.Output;
    }

    [When("^監視先の文書を書き換えて差分を照会する$")]
    public async Task ChangeAndQuery()
    {
        File.WriteAllText(SourcePath, Updated);
        _diff = await RunAsync("cli-diff", "diff", "--checkpoint", _checkpoint, "--json");
    }

    [Then("^差分には更新された文書だけが表示される$")]
    public void AssertOnlyModified()
    {
        var result = Assert.IsType<CliResult>(_diff);
        Assert.Equal(0, result.ExitCode);
        using var doc = JsonDocument.Parse(result.Output);
        var entry = Assert.Single(doc.RootElement.EnumerateArray());
        Assert.Equal("updated", entry.GetProperty("kind").GetString());
        Assert.Equal(FileName, entry.GetProperty("path").GetString());
    }

    [When("^更新分を空の保存先に収集する$")]
    public async Task Collect()
    {
        var result = await CollectAsync("cli-collect");
        Assert.Equal(0, result.ExitCode);
    }

    [Then("^更新後の文書が保存される$")]
    public void AssertCollected() => Assert.Equal(Updated, File.ReadAllText(DestinationPath));

    [When("^保存先の文書を別内容に変えて再収集する$")]
    public async Task Recollect()
    {
        // 同じ内容の再書き込みでは上書きを検出できないので保存先に別の内容を置く。
        File.WriteAllText(DestinationPath, Existing);
        var result = await CollectAsync("cli-collect-existing");
        Assert.Equal(3, result.ExitCode);
    }

    [Then("^既存の保存先と共有元の内容は変わらない$")]
    public void AssertFilesUnchanged()
    {
        Assert.Equal(Existing, File.ReadAllText(DestinationPath));
        Assert.Equal(Updated, File.ReadAllText(SourcePath));
    }

    private string SourcePath => Path.Combine(_root, FileName);
    private string DestinationPath => Path.Combine(_destination, FileName);

    private Task<CliResult> CollectAsync(string label) =>
        RunAsync(label, "collect", "--checkpoint", _checkpoint, "--output", _destination, "--json");
}
