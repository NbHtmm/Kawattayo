using System.Text.Json;
using Reqnroll;
using Xunit;
using static Kawattayo.Sct.Steps.ProductCli;

namespace Kawattayo.Sct.Steps;

[Binding]
public sealed class CheckpointVolumeSteps
{
    private readonly string _source = Directory.CreateDirectory(Path.Combine(Work, "volume-source")).FullName;
    private readonly string _output = Directory.CreateDirectory(Path.Combine(Work, "volume-output")).FullName;
    private readonly Dictionary<string, string> _expectedKinds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _expectedCopies = new(StringComparer.Ordinal);
    private string _id = "";

    [Given("^16のフォルダに計512ファイルがある状態を基準にする$")]
    public async Task CaptureFixture()
    {
        for (int group = 0; group < 16; ++group)
        {
            var folder = $"batch-{group:D2}";
            Directory.CreateDirectory(Path.Combine(_source, folder));
            for (int file = 0; file < 32; ++file)
                File.WriteAllText(Path.Combine(_source, folder, $"file-{file:D2}.txt"), $"baseline {group}/{file}");
        }

        var result = await RunAsync("volume-create", "checkpoint", "create", "--name", "volume", "--root", _source);
        Assert.Equal(0, result.ExitCode);
        Assert.Matches("^[a-f0-9]{32}$", result.Output);
        _id = result.Output;
    }

    [When("^各フォルダで2件ずつ追加・更新・削除を行う$")]
    public void ChangeFiles()
    {
        for (int group = 0; group < 16; ++group)
        {
            var folder = $"batch-{group:D2}";
            for (int file = 0; file < 2; ++file)
            {
                WriteChange(Path.Combine(folder, $"file-{file:D2}.txt"), "updated", $"final long log {group}/{file}");
                WriteChange(Path.Combine(folder, $"new-{file:D2}.txt"), "added", $"new log {group}/{file}");
                var deleted = Path.Combine(folder, $"file-{file + 2:D2}.txt");
                File.Delete(Path.Combine(_source, deleted));
                _expectedKinds.Add(deleted, "deleted");
            }
        }
    }

    [Then("^配布CLIが96件の差分を重複なく分類し64ファイルだけを収集する$")]
    public async Task AssertCompleteDiffAndCopy()
    {
        var diff = await RunAsync("volume-diff", "diff", "--checkpoint", _id, "--json");
        Assert.Equal(0, diff.ExitCode);
        using var differences = JsonDocument.Parse(diff.Output);
        var rows = differences.RootElement.EnumerateArray().ToDictionary(
            row => row.GetProperty("path").GetString()!, row => row.GetProperty("kind").GetString(), StringComparer.Ordinal);
        Assert.Equal(96, rows.Count);
        Assert.Equal(_expectedKinds.OrderBy(x => x.Key, StringComparer.Ordinal),
            rows.OrderBy(x => x.Key, StringComparer.Ordinal));

        var collect = await RunAsync("volume-collect", "collect", "--checkpoint", _id, "--output", _output, "--json");
        Assert.Equal(0, collect.ExitCode);
        using var copy = JsonDocument.Parse(collect.Output);
        Assert.Empty(copy.RootElement.GetProperty("unconfirmed").EnumerateArray());
        var copied = copy.RootElement.GetProperty("files").EnumerateArray()
            .Select(row => row.GetProperty("saved").GetString()).Order(StringComparer.Ordinal);
        var expected = _expectedCopies.Keys.Select(name => Path.Combine(_output, name)).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(64, expected.Length);
        Assert.Equal(expected, copied);
        Assert.Equal(expected, Directory.EnumerateFiles(_output, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal));
        foreach (var (name, content) in _expectedCopies)
        {
            Assert.Equal(content, File.ReadAllText(Path.Combine(_output, name)));
            Assert.Equal(content, File.ReadAllText(Path.Combine(_source, name)));
        }
    }

    private void WriteChange(string name, string kind, string content)
    {
        File.WriteAllText(Path.Combine(_source, name), content);
        _expectedKinds.Add(name, kind);
        _expectedCopies.Add(name, content);
    }
}
