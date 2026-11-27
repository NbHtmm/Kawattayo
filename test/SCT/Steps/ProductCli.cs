using System.Diagnostics;
using System.Text;

namespace Kawattayo.Sct.Steps;

internal static class ProductCli
{
    internal static string Work => RequiredEnvironment("KAWATTAYO_SCT_WORK");

    internal static async Task<CliResult> RunAsync(string label, params string[] arguments)
    {
        var start = new ProcessStartInfo(RequiredEnvironment("KAWATTAYO_SCT_CLI"))
        {
            WorkingDirectory = Work, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {label}.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var errors = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            var stdout = await output;
            var stderr = await errors;
            await File.WriteAllTextAsync(Path.Combine(Work, label + ".stdout.txt"), stdout);
            await File.WriteAllTextAsync(Path.Combine(Work, label + ".stderr.txt"), stderr);
            return new CliResult(process.ExitCode, stdout.Trim(), stderr.Trim());
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            throw new TimeoutException($"The CLI did not finish within 20 seconds: {label}.");
        }
    }

    private static string RequiredEnvironment(string name) =>
        Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException($"Run this test with test/run_sct.bat: {name} is missing.");

    internal sealed record CliResult(int ExitCode, string Output, string Error);
}
