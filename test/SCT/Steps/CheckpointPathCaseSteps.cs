using System.Text.Json;
using Reqnroll;
using Xunit;
using static Kawattayo.Sct.Steps.ProductCli;

namespace Kawattayo.Sct.Steps;

[Binding]
public sealed class CheckpointPathCaseSteps
{
    private const string Contents = "Case-only filename change fixture";
    private readonly string _source = Directory.CreateDirectory(Path.Combine(Work, "case-source")).FullName;
    private readonly string _destination = Path.Combine(Work, "must-not-be-created");
    private readonly DateTime _mtime = new(2026, 1, 24, 5, 0, 0, DateTimeKind.Utc);
    private string _id = "";

    [Given("^NTFSの試験フォルダで大文字を含むファイル名を基準にする$")]
    public async Task CaptureFixture()
    {
        Assert.Equal("NTFS", new DriveInfo(Path.GetPathRoot(_source)!).DriveFormat);
        var original = Path.Combine(_source, "REPORT.txt");
        File.WriteAllText(original, Contents);
        File.SetLastWriteTimeUtc(original, _mtime);
        var checkpoint = await RunAsync("case-create", "checkpoint", "create", "--name", "case", "--root", _source);
        Assert.Equal(0, checkpoint.ExitCode);
        Assert.Matches("^[a-f0-9]{32}$", checkpoint.Output);
        _id = checkpoint.Output;
    }

    [When("^ファイルの本文と更新時刻を保ったまま名前を小文字に変える$")]
    public void RenameCase()
    {
        var oldName = Path.Combine(_source, "REPORT.txt");
        var transient = Path.Combine(_source, "intermediate.txt");
        var newName = Path.Combine(_source, "report.txt");
        File.Move(oldName, transient);
        File.Move(transient, newName);
        Assert.Equal("report.txt", Path.GetFileName(Assert.Single(Directory.EnumerateFiles(_source))));
        Assert.Equal(_mtime, File.GetLastWriteTimeUtc(newName));
    }

    [Then("^配布CLIの差分と収集結果は空で元ファイルを保持する$")]
    public async Task VerifyNoChange()
    {
        var differences = await RunAsync("case-diff", "diff", "--checkpoint", _id, "--json");
        Assert.Equal(0, differences.ExitCode);
        using var diff = JsonDocument.Parse(differences.Output);
        Assert.Empty(diff.RootElement.EnumerateArray());

        var collection = await RunAsync("case-collect", "collect", "--checkpoint", _id, "--output", _destination, "--json");
        Assert.Equal(0, collection.ExitCode);
        using var json = JsonDocument.Parse(collection.Output);
        Assert.Empty(json.RootElement.GetProperty("files").EnumerateArray());
        Assert.Empty(json.RootElement.GetProperty("unconfirmed").EnumerateArray());
        Assert.False(Directory.Exists(_destination));
        Assert.Equal(Contents, File.ReadAllText(Path.Combine(_source, "report.txt")));
    }
}
