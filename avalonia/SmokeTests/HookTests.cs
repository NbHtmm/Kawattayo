#if KAWATTAYO_SMOKE
namespace ShareFileWatch;

internal static class HookTests
{
    internal static async Task Run(string fixture)
    {
        var folder = Directory.CreateDirectory(Path.Combine(fixture, "hook-" + Guid.NewGuid().ToString("N"))).FullName;
        string source = Path.Combine(folder, "計測 & 引数 %1.csv");
        string output = Path.Combine(folder, "argument.txt");
        string config = Path.Combine(folder, "hooks.json");
        File.WriteAllText(source, "original");
        var fingerprint = FileProbe.Read(source).Fingerprint!;
        var id = Guid.NewGuid();
        using var service = new UpdateHooks(folder, config);
        var rule = new HookRule(folder, Environment.ProcessPath!, ["--hook-test-probe", output], 0);
        service.Set(id, rule);
        service.Enqueue(id, rule, source, fingerprint);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!service.Status(id).StartsWith("終了コード", StringComparison.Ordinal))
            await Task.Delay(50, timeout.Token);
        if (File.ReadAllText(output) != source || !service.Status(id).StartsWith("終了コード 0"))
            throw new InvalidOperationException("Hook did not preserve the file as one literal argument.");
        File.Delete(output);
        File.AppendAllText(source, " updated");
        service.Enqueue(id, rule, source, fingerprint);
        while (!service.Status(id).StartsWith("再更新", StringComparison.Ordinal))
            await Task.Delay(50, timeout.Token);
        if (File.Exists(output)) throw new InvalidOperationException("A stale update launched a hook.");
        using var loaded = new UpdateHooks(folder, config);
        if (loaded.Get(id, folder)?.Arguments.Length != 2) throw new InvalidOperationException("Hook rules did not persist.");
        service.Set(id, null);
        if (service.Get(id, folder) is not null) throw new InvalidOperationException("Hook was not disabled.");
    }
}
#endif
