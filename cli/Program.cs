using System.Text;
using System.Text.Json;
using ShareFileWatch;

internal static class Program
{
    private const string Usage = """
        Kawattayo-cli
          checkpoint create --name NAME --root ABSOLUTE_PATH [--no-recursive] [--exclude GLOB ...]
          checkpoint list
          diff --checkpoint ID_OR_NAME [--json]
          collect --checkpoint ID_OR_NAME --output DIRECTORY [--json]
        終了コード: 0 成功 / 2 使用法 / 3 確認不能・保存失敗 / 4 処理失敗 / 130 中断
        """;

    private static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        if (args.Length == 0 || args is ["--help"] or ["-h"]) { Console.WriteLine(Usage); return 0; }
        using var cancel = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, e) => { e.Cancel = true; cancel.Cancel(); };
        Console.CancelKeyPress += onCancel;
        try { return Run(args, new Checkpoints(AppContext.BaseDirectory), cancel.Token); }
        catch (OperationCanceledException) { return 130; }
        catch (ArgumentException ex) { Console.Error.WriteLine(ex.Message); return 2; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { Console.Error.WriteLine(ex.Message); return 4; }
        finally { Console.CancelKeyPress -= onCancel; }
    }

    private static int Run(string[] args, Checkpoints points, CancellationToken stop)
    {
        if (args is ["checkpoint", "list"])
        {
            foreach (var item in points.List()) Console.WriteLine($"{item.Id}\t{item}");
            return 0;
        }
        if (args is ["checkpoint", "create", .. var fields])
        {
            var o = new Options(fields, ["--name", "--root", "--exclude"], ["--no-recursive"]);
            var point = points.Create(o.One("--name"), o.One("--root"), !o.Has("--no-recursive"), string.Join('\n', o.All("--exclude")), stop);
            Console.WriteLine(point.Id);
            return 0;
        }
        if (args[0] is not ("diff" or "collect")) throw new ArgumentException(Usage);
        bool collect = args[0] == "collect";
        var options = new Options(args[1..], collect ? ["--checkpoint", "--output"] : ["--checkpoint"], ["--json"]);
        string? output = collect ? options.One("--output") : null;
        string id = Resolve(points, options.One("--checkpoint"));
        var result = points.Compare(id, stop);
        var unknown = result.Changes.Where(x => x.Kind == DifferenceKind.Unconfirmed).ToArray();
        if (!collect)
        {
            if (options.Has("--json")) Json(w => Changes(w, result.Changes));
            else foreach (var change in result.Changes) Console.WriteLine(change);
            return unknown.Length == 0 ? 0 : 3;
        }
        var copies = Checkpoints.Collect(result.Info, result.Changes, output!, stop);
        stop.ThrowIfCancellationRequested();
        if (options.Has("--json")) Json(w => Copies(w, copies, unknown));
        else
        {
            foreach (var copy in copies) Console.WriteLine(copy.Saved ?? $"未保存：{copy.Source} ｜ {copy.Error}");
            foreach (var skipped in unknown) Console.WriteLine(skipped);
        }
        return unknown.Length == 0 && copies.All(x => x.Saved is not null) ? 0 : 3;
    }

    private static string Resolve(Checkpoints points, string token)
    {
        var items = points.List();
        if (items.FirstOrDefault(x => string.Equals(x.Id, token, StringComparison.OrdinalIgnoreCase)) is { } id) return id.Id;
        var names = items.Where(x => string.Equals(x.Name, token, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        return names.Length switch
        {
            1 => names[0].Id, 0 => throw new IOException("チェックポイントが見つかりません。"),
            _ => throw new ArgumentException("同名のチェックポイントがあります。一覧からIDを指定してください。")
        };
    }

    private static void Changes(Utf8JsonWriter writer, IEnumerable<CheckpointDifference> changes)
    {
        writer.WriteStartArray();
        foreach (var change in changes)
        {
            writer.WriteStartObject();
            writer.WriteString("path", change.Relative);
            writer.WriteString("kind", change.Kind.ToString().ToLowerInvariant());
            if (change.Error is not null) writer.WriteString("error", change.Error);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void Copies(Utf8JsonWriter writer, DownloadResult[] copies, CheckpointDifference[] unknown)
    {
        writer.WriteStartObject();
        writer.WriteStartArray("files");
        foreach (var copy in copies)
        {
            writer.WriteStartObject();
            writer.WriteString("source", copy.Source);
            writer.WriteString("saved", copy.Saved);
            writer.WriteString("error", copy.Error);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WritePropertyName("unconfirmed");
        Changes(writer, unknown);
        writer.WriteEndObject();
    }

    private static void Json(Action<Utf8JsonWriter> write)
    {
        using var stdout = Console.OpenStandardOutput();
        using (var writer = new Utf8JsonWriter(stdout)) write(writer);
        stdout.WriteByte((byte)'\n');
    }

    private sealed class Options
    {
        private readonly Dictionary<string, List<string>> values = new(StringComparer.Ordinal);
        internal bool Has(string key) => values.ContainsKey(key);
        internal IReadOnlyList<string> All(string key) => values.GetValueOrDefault(key) ?? [];
        internal string One(string key) => All(key).FirstOrDefault() ?? throw new ArgumentException(key + " を指定してください。");

        internal Options(string[] args, string[] fields, string[] flags)
        {
            for (int i = 0; i < args.Length; i++)
            {
                string key = args[i];
                string value;
                if (flags.Contains(key)) value = "";
                else if (fields.Contains(key) && i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    value = args[++i];
                else throw new ArgumentException("引数を確認してください：" + key);
                if (!values.TryGetValue(key, out var list)) values[key] = list = [];
                else if (key != "--exclude") throw new ArgumentException(key + " が複数回指定されています。");
                list.Add(value);
            }
        }
    }
}
