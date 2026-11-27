using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Reqnroll;
using Xunit;
using static Kawattayo.Sct.Steps.ProductCli;

namespace Kawattayo.Sct.Steps;

[Binding]
public sealed class CheckpointJunctionSteps
{
    private const string OrdinaryFile = "report.txt";
    private const string OrdinaryContent = "Ordinary local report";
    private readonly string _source = Directory.CreateDirectory(Path.Combine(Work, "junction-source")).FullName;
    private readonly string _sibling = Directory.CreateDirectory(Path.Combine(Work, "unrelated-fixture")).FullName;
    private readonly string _ordinaryOutput = Path.Combine(Work, "ordinary-output");
    private readonly string _linkedOutput = Path.Combine(Work, "linked-output");
    private string _checkpoint = "";

    [Given("^NTFSの試験領域内だけで通常フォルダとジャンクションを用意する$")]
    public async Task PrepareFixture()
    {
        Assert.Equal("NTFS", new DriveInfo(Path.GetPathRoot(_source)!).DriveFormat);
        var created = await RunAsync("junction-create", "checkpoint", "create", "--name", "junction", "--root", _source);
        Assert.Equal(0, created.ExitCode);
        Assert.Matches("^[a-f0-9]{32}$", created.Output);
        _checkpoint = created.Output;
    }

    [When("^通常ファイルと参照先のファイルを追加して比較と収集を行う$")]
    public async Task VerifySourceAndRegularCopy()
    {
        var link = Path.Combine(_source, "alias");
        try
        {
            await CreateFixtureJunction(link, _sibling);
            File.WriteAllText(Path.Combine(_source, OrdinaryFile), OrdinaryContent);
            File.WriteAllText(Path.Combine(_sibling, "unrelated.txt"), "Unrelated fixture text");

            var diff = await RunAsync("junction-diff", "diff", "--checkpoint", _checkpoint, "--json");
            Assert.Equal(0, diff.ExitCode);
            using var differences = JsonDocument.Parse(diff.Output);
            var row = Assert.Single(differences.RootElement.EnumerateArray());
            Assert.Equal(OrdinaryFile, row.GetProperty("path").GetString());
            Assert.Equal("added", row.GetProperty("kind").GetString());

            var collect = await RunAsync("junction-collect-regular", "collect", "--checkpoint", _checkpoint, "--output", _ordinaryOutput, "--json");
            Assert.Equal(0, collect.ExitCode);
            using var copied = JsonDocument.Parse(collect.Output);
            Assert.Empty(copied.RootElement.GetProperty("unconfirmed").EnumerateArray());
            var expected = Path.Combine(_ordinaryOutput, OrdinaryFile);
            Assert.Equal(expected, Assert.Single(copied.RootElement.GetProperty("files").EnumerateArray()).GetProperty("saved").GetString());
            Assert.Equal(expected, Assert.Single(Directory.EnumerateFileSystemEntries(_ordinaryOutput)));
            Assert.Equal(OrdinaryContent, File.ReadAllText(expected));
            Assert.Equal("Unrelated fixture text", File.ReadAllText(Path.Combine(_sibling, "unrelated.txt")));
        }
        finally { DeleteFixtureJunction(link); }
    }

    [Then("^配布CLIはジャンクションを保存先に選ぶと拒否し既存ファイルを保つ$")]
    public async Task VerifyUnsupportedOutputFolder()
    {
        try
        {
            await CreateFixtureJunction(_linkedOutput, _sibling);
            var collect = await RunAsync("junction-collect-unsupported", "collect", "--checkpoint", _checkpoint, "--output", _linkedOutput, "--json");
            Assert.Equal(3, collect.ExitCode);
            using var result = JsonDocument.Parse(collect.Output);
            var file = Assert.Single(result.RootElement.GetProperty("files").EnumerateArray());
            Assert.Equal(Path.Combine(_source, OrdinaryFile), file.GetProperty("source").GetString());
            Assert.Equal(JsonValueKind.Null, file.GetProperty("saved").ValueKind);
            Assert.False(string.IsNullOrWhiteSpace(file.GetProperty("error").GetString()));
            var existing = Path.Combine(_sibling, "unrelated.txt");
            Assert.Equal(existing, Assert.Single(Directory.EnumerateFileSystemEntries(_sibling)));
            Assert.Equal("Unrelated fixture text", File.ReadAllText(existing));
            Assert.Equal(OrdinaryContent, File.ReadAllText(Path.Combine(_source, OrdinaryFile)));
        }
        finally { DeleteFixtureJunction(_linkedOutput); }
    }

    // The OS fixture receives absolute paths as data. The command itself never interpolates them.
    private static async Task CreateFixtureJunction(string link, string target)
    {
        const string command = "$ErrorActionPreference = 'Stop'; New-Item -ItemType Junction -Path $env:SCT_JUNCTION -Target $env:SCT_TARGET | Out-Null";
        var shell = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
        var start = new ProcessStartInfo(shell)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true
        };
        foreach (var part in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(command)) })
            start.ArgumentList.Add(part);
        start.Environment["SCT_JUNCTION"] = link;
        start.Environment["SCT_TARGET"] = target;
        using var process = Process.Start(start) ?? throw new IOException("Unable to start the fixture shell.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var errors = process.StandardError.ReadToEndAsync(timeout.Token);
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw new TimeoutException("The junction fixture did not initialize.");
        }
        Assert.True(process.ExitCode == 0, await errors);
        Assert.True(File.GetAttributes(link).HasFlag(FileAttributes.ReparsePoint));
        Assert.Equal(target, Directory.ResolveLinkTarget(link, returnFinalTarget: true)?.FullName);
    }

    private static void DeleteFixtureJunction(string path)
    {
        if (Directory.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            Directory.Delete(path, recursive: false); // Unlink only; never walk the target.
    }
}
