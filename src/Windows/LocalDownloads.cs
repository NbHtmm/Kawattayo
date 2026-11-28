namespace ShareFileWatch;

internal sealed class LocalDownloads : ICheckpointDownloads
{
    private const string Zone = ":Zone.Identifier";

    // 保存は拡張子を問わないが、実行形式やショートカットをアプリから直接起動しない。
    private static readonly HashSet<string> Documents = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".log", ".csv", ".tsv", ".pdf", ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp",
        ".docx", ".xlsx", ".pptx", ".odt", ".ods", ".odp"
    };

    internal static bool CanOpen(string path) => Documents.Contains(Path.GetExtension(path));


    public DownloadResult[] Save(string folder, IReadOnlyList<DownloadRequest> requests, CancellationToken stop) =>
        SaveBatch(folder, requests, stop, SaveOne);

    // Keep completed results. Do not invoke the publisher for items not started when cancellation arrives.
    internal static DownloadResult[] SaveBatch(string folder, IReadOnlyList<DownloadRequest> requests, CancellationToken stop, Func<string, DownloadRequest, CancellationToken, DownloadResult> publish)
    {
        var results = new DownloadResult[requests.Count];
        for (int i = 0; i < requests.Count; i++)
        {
            var request = requests[i];
            results[i] = stop.IsCancellationRequested ? new(request.Source, null, "キャンセル") : publish(folder, request, stop);
        }
        return results;
    }

    private static DownloadResult SaveOne(string folder, DownloadRequest request, CancellationToken stop)
    {
        string? staging = null;
        bool ownsStaging = false;
        try
        {
            stop.ThrowIfCancellationRequested();
            var destination = ResolveDestination(folder, request, static drive => new DriveInfo(drive).DriveType);

            if (request.SourceRoot is { } sourceRoot) EnsureSourceDirectories(request.Source, sourceRoot, File.GetAttributes);
            var expected = FileProbe.Read(request.Source).Fingerprint
                ?? throw new IOException("元ファイルを確認できません。");
            if (request.Expected is { } preview && preview != expected)
                throw new IOException("比較後に元ファイルが変化しました。再比較してください。");
            var parent = Path.GetDirectoryName(destination)!;
            PrepareDestination(parent);
            if (File.Exists(destination) || Directory.Exists(destination))
                throw new IOException("同名の項目があるため、上書きしませんでした。");

            staging = Path.Combine(parent, $".kawattayo-{Guid.NewGuid():N}.part");
            using (var input = OpenShared(request.Source))
            using (var output = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.SequentialScan))
            {
                ownsStaging = true;
                input.CopyToAsync(output, stop).GetAwaiter().GetResult();
                if ((ulong)output.Length != expected.Bytes) throw new IOException("コピー中に元ファイルが変化しました。");
            }

            // バイト列だけのコピーで Windows のゾーン情報を落とさない。
            CopyZone(request.Source, staging, stop);
            if (FileProbe.Read(request.Source).Fingerprint != expected)
                throw new IOException("コピー中に元ファイルが変化したか、共有先を確認できません。");
            stop.ThrowIfCancellationRequested();

            // 同じフォルダ内で完成品に切り替え、競合して作られた同名ファイルも上書きしない。
            File.Move(staging, destination, overwrite: false);
            ownsStaging = false;
            return new(request.Source, destination, null);
        }
        catch (OperationCanceledException) { return new(request.Source, null, "キャンセル"); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new(request.Source, null, error.Message);
        }
        finally
        {
            if (ownsStaging)
            {
                try { File.Delete(staging!); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    // Canonicalize both path components before applying the local-drive and output-scope policy.
    internal static string ResolveDestination(string folder, DownloadRequest request, Func<string, DriveType> driveType)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        var drive = Path.GetPathRoot(root);
        if (drive?.Length != 3 || driveType(drive) is not (DriveType.Fixed or DriveType.Removable or DriveType.Ram))
            throw new IOException("ローカルドライブのフォルダを選択してください。");
        var destination = Path.GetFullPath(Path.Combine(root, request.Relative));
        var relative = Path.GetRelativePath(root, destination);
        if (Path.IsPathRooted(relative) || relative is "." or ".." || relative.StartsWith(@"..\") ||
            string.Equals(destination, request.Source, StringComparison.OrdinalIgnoreCase))
            throw new IOException("保存先に元ファイルや指定フォルダの外は選べません。");
        return destination;
    }

    private static FileStream OpenShared(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.SequentialScan);

    private static void CopyZone(string source, string staging, CancellationToken stop)
    {
        FileStream zone;
        try { zone = OpenShared(source + Zone); }
        catch (FileNotFoundException) { return; }
        // SMB 実装や FAT など、名前付きストリームを元々扱わない共有元もある。
        catch (IOException ex) when ((ex.HResult & 0xffff) is 1 or 50 or 123) { return; }
        using (zone)
        using (var output = new FileStream(staging + Zone, FileMode.Create, FileAccess.Write, FileShare.None))
            zone.CopyToAsync(output, stop).GetAwaiter().GetResult();
    }

    internal static void EnsureSourceDirectories(string source, string root, Func<string, FileAttributes> attributes)
    {
        // 比較後に親フォルダがジャンクションへ差し替わっていないか確認する。
        for (var parent = Path.GetDirectoryName(source); parent is not null; parent = Path.GetDirectoryName(parent))
        {
            if ((attributes(parent) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("リンクを経由する元ファイルはコピーできません。");
            if (string.Equals(Path.TrimEndingDirectorySeparator(parent), root, StringComparison.OrdinalIgnoreCase)) return;
        }
        throw new IOException("元ファイルが対象フォルダの外にあります。");
    }

    private static void PrepareDestination(string parent) =>
        PrepareDestination(parent, static path => Directory.CreateDirectory(path).Attributes);

    internal static void PrepareDestination(string parent, Func<string, FileAttributes> createDirectory)
    {
        // 既存のジャンクションなどをたどり、意図せず共有先へ書き込まない。
        var parents = new Stack<string>();
        for (string? path = parent; path is not null; path = Path.GetDirectoryName(path)) parents.Push(path);
        while (parents.TryPop(out var directory))
        {
            if ((createDirectory(directory) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("リンクやジャンクションを含まない保存先を選択してください。");
        }
    }
}
