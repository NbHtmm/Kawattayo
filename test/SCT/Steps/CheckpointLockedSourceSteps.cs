using System.Text.Json;
using Reqnroll;
using Xunit;
using static Kawattayo.Sct.Steps.ProductCli;

namespace Kawattayo.Sct.Steps;

[Binding]
public sealed class CheckpointLockedSourceSteps
{
    private const string FileName = "being-edited.txt";
    private const string Content = "Bench fixture written exclusively, not yet available to a reader.";
    private readonly string _source = Directory.CreateDirectory(Path.Combine(Work, "locked-source")).FullName;
    private readonly string _output = Directory.CreateDirectory(Path.Combine(Work, "locked-output")).FullName;
    private string _id = "";
    private CliResult? _first;

    [Given("^編集競合を検証するため空のローカルフォルダを基準にする$")]
    public async Task CaptureFixture()
    {
        var checkpoint = await RunAsync("lock-create", "checkpoint", "create", "--name", "source lock", "--root", _source);
        Assert.Equal(0, checkpoint.ExitCode);
        Assert.Matches("^[a-f0-9]{32}$", checkpoint.Output);
        _id = checkpoint.Output;
    }

    [When("^試験文書を書き込み用の排他ハンドルで開いたままにして収集を実行する$")]
    public async Task TryWhileWriterIsActive()
    {
        var source = Path.Combine(_source, FileName);
        File.WriteAllText(source, Content);
        using (new FileStream(source, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            _first = await RunAsync("lock-collect-before", "collect", "--checkpoint", _id, "--output", _output, "--json");
            // Check the whole output while the failure still holds, including staging files.
            Assert.Empty(Directory.EnumerateFileSystemEntries(_output));
        }
    }

    [Then("^配布CLIは収集できなかったと報告し成果物を残さず解除後は収集できる$")]
    public async Task VerifyFailureAndRetry()
    {
        var blocked = Assert.IsType<CliResult>(_first);
        Assert.Equal(3, blocked.ExitCode);
        using var firstOutput = JsonDocument.Parse(blocked.Output);
        var failed = Assert.Single(firstOutput.RootElement.GetProperty("files").EnumerateArray());
        Assert.Equal(Path.Combine(_source, FileName), failed.GetProperty("source").GetString());
        Assert.Equal(JsonValueKind.Null, failed.GetProperty("saved").ValueKind);
        Assert.False(string.IsNullOrWhiteSpace(failed.GetProperty("error").GetString()));

        var retry = await RunAsync("lock-collect-retry", "collect", "--checkpoint", _id, "--output", _output, "--json");
        Assert.Equal(0, retry.ExitCode);
        using var retryOutput = JsonDocument.Parse(retry.Output);
        var success = Assert.Single(retryOutput.RootElement.GetProperty("files").EnumerateArray());
        var destination = Path.Combine(_output, FileName);
        Assert.Equal(destination, success.GetProperty("saved").GetString());
        Assert.Equal(destination, Assert.Single(Directory.EnumerateFileSystemEntries(_output)));
        Assert.Equal(Content, File.ReadAllText(destination));
        Assert.Equal(Content, File.ReadAllText(Path.Combine(_source, FileName)));
    }
}
