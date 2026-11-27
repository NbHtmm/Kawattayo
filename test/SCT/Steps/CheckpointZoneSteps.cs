using System.Text;
using System.Text.Json;
using Reqnroll;
using Xunit;
using static Kawattayo.Sct.Steps.ProductCli;

namespace Kawattayo.Sct.Steps;

[Binding]
public sealed class CheckpointZoneSteps
{
    private const string Name = "report with provenance.txt";
    private const string Body = "Plain-text test fixture, not executed.";
    private const string ZoneStream = ":Zone.Identifier";
    private static readonly byte[] Policy = Encoding.ASCII.GetBytes("[ZoneTransfer]\r\nZoneId=3\r\n");
    private readonly string _source = Directory.CreateDirectory(Path.Combine(Work, "zone-source")).FullName;
    private readonly string _output = Directory.CreateDirectory(Path.Combine(Work, "zone-output")).FullName;
    private string _id = "";

    [Given("^NTFS上の空の試験フォルダを差分の基準にする$")]
    public async Task CaptureFixture()
    {
        var drive = new DriveInfo(Path.GetPathRoot(_source)!);
        Assert.Equal("NTFS", drive.DriveFormat);
        var checkpoint = await RunAsync("zone-create", "checkpoint", "create", "--name", "zone", "--root", _source);
        Assert.Equal(0, checkpoint.ExitCode);
        Assert.Matches("^[a-f0-9]{32}$", checkpoint.Output);
        _id = checkpoint.Output;
    }

    [When("^Windowsのゾーン情報が付いた試験用テキストを追加する$")]
    public void AddDocument()
    {
        var source = Path.Combine(_source, Name);
        File.WriteAllText(source, Body);
        File.WriteAllBytes(source + ZoneStream, Policy);
    }

    [Then("^配布CLIで収集した文書の本文とゾーン情報は元と完全に一致する$")]
    public async Task VerifyCopy()
    {
        var source = Path.Combine(_source, Name);
        var saved = Path.Combine(_output, Name);
        var collect = await RunAsync("zone-collect", "collect", "--checkpoint", _id, "--output", _output, "--json");
        Assert.Equal(0, collect.ExitCode);
        using var json = JsonDocument.Parse(collect.Output);
        Assert.Empty(json.RootElement.GetProperty("unconfirmed").EnumerateArray());
        var copy = Assert.Single(json.RootElement.GetProperty("files").EnumerateArray());
        Assert.Equal(saved, copy.GetProperty("saved").GetString());
        Assert.Equal(JsonValueKind.Null, copy.GetProperty("error").ValueKind);
        Assert.Equal(Body, File.ReadAllText(saved));
        Assert.Equal(Body, File.ReadAllText(source));
        Assert.Equal(Policy, File.ReadAllBytes(saved + ZoneStream));
        Assert.Equal(Policy, File.ReadAllBytes(source + ZoneStream));
        Assert.Equal(saved, Assert.Single(Directory.EnumerateFiles(_output)));
    }
}
