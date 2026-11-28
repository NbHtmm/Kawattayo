using System.Text.Json;

namespace ShareFileWatch;

// JSON設定の検証・旧バックアップからの復旧・一時ファイル経由の書き込みを担当する。
internal sealed class SettingsFile(string file, SettingsFile.IStorage? backend = null)
{
    private readonly string __backup = file + ".bak";
    private readonly string __temporary = file + ".tmp";
    private readonly IStorage storage = backend ?? new DiskStorage();
    private string? __savedSettings;

    internal void EnsurePresent()
    {
        if (!storage.Exists(file) && !storage.Exists(__backup)) WriteSettings(SerializeSettings(new()));
    }

    internal void Save(AppState state)
    {
        var json = SerializeSettings(state);
        if (json != __savedSettings || !storage.Exists(file)) WriteSettings(json);
    }

    internal (AppState State, string? Warning) Load()
    {
        if (!storage.Exists(file) && !storage.Exists(__backup)) return (new(), null);
        try
        {
            var state = ReadSettings(file);
            __savedSettings = SerializeSettings(state);
            return (state, null);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return RecoverSettings(ex);
        }
    }

    private (AppState State, string Warning) RecoverSettings(Exception primaryError)
    {
        AppState state;
        try { state = ReadSettings(__backup); }
        catch (Exception backupError) when (backupError is IOException or JsonException or UnauthorizedAccessException)
        {
            throw new IOException("設定とバックアップを読み込めません。元のファイルは上書きしていません。",
                new AggregateException(primaryError, backupError));
        }
        if (storage.Exists(file)) storage.Move(file, file + ".broken-" + Guid.NewGuid().ToString("N"));
        WriteSettings(SerializeSettings(state));
        return (state, "メインの設定を読み込めなかったため、バックアップから復元しました。");
    }

    private AppState ReadSettings(string path)
    {
        using var stream = storage.OpenRead(path);
        return DeserializeSettings(stream);
    }

    // ファイルを開く処理と分け、メモリ上の設定にも同じ読み取り・検証ルールを適用する。
    internal static AppState DeserializeSettings(Stream stream)
    {
        var data = JsonSerializer.Deserialize(stream, SettingsJsonContext.Default.AppState) ?? throw new JsonException();
        return ValidateSettings(data);
    }

    // DTO の内容とパス文字列だけを検証する。設定ファイルの読み取りや復旧は行わない。
    internal static AppState ValidateSettings(AppState data)
    {
        if (data.Version != 1) throw new NotSupportedException("この設定ファイルのバージョンには対応していません。");
        if (data.Items is null || data.Items.Any(i => i is null || i.Id == Guid.Empty || string.IsNullOrWhiteSpace(i.Path)) ||
            data.Items.Select(i => i.Id).Distinct().Count() != data.Items.Count)
            throw new JsonException("監視リストが壊れています。");

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in data.Items)
        {
            try { item.Path = item.IsFolder ? FileProbe.NormalizeFolder(item.Path) : FileProbe.Normalize(item.Path); }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
            { throw new JsonException("監視対象のパスが無効です。", ex); }
            if (!paths.Add(item.Path)) throw new JsonException("監視対象のパスが重複しています。");
        }
        data.RecursiveFolders ??= true;
        data.ExcludedFilePatterns = FileExclusions.Normalize(data.ExcludedFilePatterns);
        data.PollSeconds = Math.Clamp(data.PollSeconds, AppDefaults.MinimumPollSeconds, AppDefaults.MaximumPollSeconds);
        data.StableSeconds = Math.Clamp(data.StableSeconds, 1, 300);
        return data;
    }

    internal static string SerializeSettings(AppState state) => JsonSerializer.Serialize(state, SettingsJsonContext.Default.AppState);

    private void WriteSettings(string settings)
    {
        storage.CreateDirectory(Path.GetDirectoryName(file)!);
        try
        {
            storage.WriteTemporary(__temporary, settings);
            // 書き換えは従来通り一括で行うが、新しいバックアップは作らない。
            if (storage.Exists(file)) storage.Replace(__temporary, file);
            else storage.Move(__temporary, file);
            __savedSettings = settings;
        }
        finally
        {
            if (storage.Exists(__temporary)) storage.Delete(__temporary);
        }
    }

    // OS への書き出しだけを分離し、設定の判断やエラー回復は実際のアプリと同じコードで試験する。
    internal interface IStorage
    {
        bool Exists(string path);
        Stream OpenRead(string path);
        void Move(string source, string destination);
        void Replace(string source, string destination);
        void CreateDirectory(string path);
        void WriteTemporary(string path, string text);
        void Delete(string path);
    }

    private sealed class DiskStorage : IStorage
    {
        public bool Exists(string path) => File.Exists(path);
        public Stream OpenRead(string path) => File.OpenRead(path);
        public void Move(string source, string destination) => File.Move(source, destination);
        public void Replace(string source, string destination) => File.Replace(source, destination, null, true);
        public void CreateDirectory(string path) => Directory.CreateDirectory(path);
        public void Delete(string path) => File.Delete(path);

        public void WriteTemporary(string path, string text)
        {
            using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            stream.Write(System.Text.Encoding.UTF8.GetBytes(text));
            stream.Flush(flushToDisk: true);
        }
    }

}
