using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace ShareFileWatch;

internal sealed record HookRule(string Target, string Executable, string[] Arguments, int WaitSeconds);

[JsonSerializable(typeof(Dictionary<Guid, HookRule>))]
internal sealed partial class HookJsonContext : JsonSerializerContext;

internal sealed class UpdateHooks : IDisposable
{
    private readonly string file;
    private readonly ConcurrentDictionary<Guid, HookRule> rules;
    private readonly ConcurrentDictionary<Guid, string> results = new();
    private readonly CancellationTokenSource stop = new();
    private readonly Channel<Job> queue = Channel.CreateBounded<Job>(new BoundedChannelOptions(4096) { SingleReader = true });
    private readonly Task runner;
    private readonly string? loadError;
    private sealed record Job(Guid Id, HookRule Rule, string Path, Fingerprint Expected, long Due);

    internal UpdateHooks(string profile, string? storage = null)
    {
        // 共有や配布フォルダ内の settings.json を読み込むだけでは自動実行を有効にしない。
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(profile).ToUpperInvariant())));
        file = storage ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Kawattayo", "Hooks", key + ".json");
        try
        {
            using var input = File.OpenRead(file);
            rules = new(JsonSerializer.Deserialize(input, HookJsonContext.Default.DictionaryGuidHookRule) ?? []);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { rules = new(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { rules = new(); loadError = "フック設定を読めません：" + error.Message; }
        runner = Task.Run(() => Run(stop.Token));
    }

    internal HookRule? Get(Guid id, string target) =>
        rules.TryGetValue(id, out var rule) && string.Equals(rule.Target, target, StringComparison.OrdinalIgnoreCase) ? rule : null;
    internal string Status(Guid id) => results.GetValueOrDefault(id) ?? loadError ?? "実行履歴はありません。";

    internal void Set(Guid id, HookRule? rule)
    {
        if (loadError is not null) throw new IOException(loadError);
        if (rule is not null) ValidateExecutable(rule.Executable);
        var next = rules.ToDictionary(x => x.Key, x => x.Value);
        if (rule is null) next.Remove(id); else next[id] = rule;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                JsonSerializer.Serialize(output, next, HookJsonContext.Default.DictionaryGuidHookRule);
            File.Move(temp, file, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }

        if (rule is null) rules.TryRemove(id, out _); else rules[id] = rule;
        results.TryRemove(id, out _);
    }

    // 登録解除後には待機中の実行も取り消す。GUID は新規登録と共有しない。
    internal void Disable(Guid id)
    {
        rules.TryRemove(id, out _);
        results.TryRemove(id, out _);
    }

    internal void Enqueue(Guid id, HookRule rule, string path, Fingerprint expected)
    {
        var job = new Job(id, rule, path, expected, Environment.TickCount64 + rule.WaitSeconds * 1000L);
        if (!queue.Writer.TryWrite(job)) results[id] = "フックの待ち行列が満杯のため、一部実行を見送りました。";
    }

    private async Task Run(CancellationToken token)
    {
        try
        {
            await foreach (var job in queue.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                if (Get(job.Id, job.Rule.Target) != job.Rule) continue;
                long delay = job.Due - Environment.TickCount64;
                if (delay > 0) await Task.Delay(TimeSpan.FromMilliseconds(delay), token).ConfigureAwait(false);
                await Execute(job, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private async Task Execute(Job job, CancellationToken token)
    {
        if (Get(job.Id, job.Rule.Target) != job.Rule) return;
        try
        {
            token.ThrowIfCancellationRequested();
            // 安定待ちの間に変わった版は走らせない。新しい版を検知すれば別途キューに入る。
            if (FileProbe.Read(job.Path).Fingerprint != job.Expected)
            {
                results[job.Id] = "再更新されたか読み取れないため実行しませんでした：" + job.Path;
                return;
            }
            ValidateExecutable(job.Rule.Executable);
            if (Get(job.Id, job.Rule.Target) != job.Rule || token.IsCancellationRequested) return;
            using var process = Process.Start(StartInfo(job.Rule, job.Path)) ?? throw new IOException("プロセスを起動できません。");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                try { process.Kill(); } catch (InvalidOperationException) { }
                results[job.Id] = "タイムアウトにより終了しました：" + job.Path;
                return;
            }
            results[job.Id] = $"終了コード {process.ExitCode}：{job.Path}";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or Win32Exception)
        { results[job.Id] = "フック実行失敗：" + error.Message; }
    }

    internal static ProcessStartInfo StartInfo(HookRule rule, string path)
    {
        var start = new ProcessStartInfo(rule.Executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(rule.Executable)!
        };
        foreach (var argument in rule.Arguments) start.ArgumentList.Add(argument);
        // 外部ユーザーが命名したパスも、コマンドではなく一つのリテラル引数として渡す。
        start.ArgumentList.Add(path);
        return start;
    }

    private static void ValidateExecutable(string path) =>
        ValidateExecutable(path, static drive => new DriveInfo(drive).DriveType, File.Exists, File.GetAttributes);

    // Keep path decisions testable without probing a real drive or launching the configured command.
    internal static void ValidateExecutable(string path, Func<string, DriveType> driveType,
        Func<string, bool> fileExists, Func<string, FileAttributes> attributes)
    {
        if (!Path.IsPathFullyQualified(path) || !string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase) ||
            Path.GetPathRoot(path) is not { Length: 3 } drive || driveType(drive) != DriveType.Fixed || !fileExists(path))
            throw new IOException("ローカル固定ドライブ上の存在する .exe を選んでください。");
        if (Path.GetFileNameWithoutExtension(path).ToLowerInvariant() is
            "cmd" or "powershell" or "pwsh" or "wscript" or "cscript" or "mshta" or "bash" or "sh" or "wsl")
            throw new IOException("シェルやスクリプトホストは直接指定せず、ファイルを引数に取る実行ファイルを指定してください。");
        for (string? part = path; part is not null; part = Path.GetDirectoryName(part))
            if ((attributes(part) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("リンクやジャンクション経由の実行ファイルは登録できません。");
    }

    public void Dispose()
    {
        queue.Writer.TryComplete();
        stop.Cancel();
        _ = runner.ContinueWith(_ => stop.Dispose(), TaskScheduler.Default);
    }
}
