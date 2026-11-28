#if KAWATTAYO_SMOKE
namespace ShareFileWatch;

internal static class UnreadDownloadTests
{
    internal static void TestDownloads(string root)
    {
        var source = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
        var destination = Directory.CreateDirectory(Path.Combine(root, "copies")).FullName;
        var doc = Path.Combine(source, "図面.txt");
        const string tag = "[ZoneTransfer]\r\nZoneId=3\r\n";
        File.WriteAllText(doc, "sample");
        File.WriteAllText(doc + ":Zone.Identifier", tag);

        DownloadRequest[] requests = [new(doc, @"資料\図面.txt"), new(doc, "図面.txt")];
        var existing = Path.Combine(destination, "図面.txt");
        File.WriteAllText(existing, "existing");
        var result = new LocalDownloads().Save(destination, requests, CancellationToken.None);
        Require(result[0].Saved == Path.Combine(destination, requests[0].Relative) && result[1].Error is not null,
            "一括保存は相対階層を保持し、成功と同名失敗を区別する");
        Require(File.ReadAllText(result[0].Saved!) == "sample" && File.ReadAllText(existing) == "existing"
            && File.ReadAllText(result[0].Saved! + ":Zone.Identifier") == tag, "既存ファイルと元のゾーン情報を保持");
        Require(!LocalDownloads.CanOpen("x.exe") && !LocalDownloads.CanOpen("x.lnk") && LocalDownloads.CanOpen("x.PDF"),
            "自動で開く形式を文書と画像などに限定");

        var locked = Path.Combine(source, "書き込み中.txt");
        File.WriteAllText(locked, "busy");
        using (File.Open(locked, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            var failed = new LocalDownloads().Save(destination, [new(locked, "書き込み中.txt")], CancellationToken.None).Single();
            Require(failed.Error is not null && !File.Exists(Path.Combine(destination, "書き込み中.txt")),
                "排他的に編集中なら保存せず、共有元を変更しない");
        }
        var aborted = new LocalDownloads().Save(destination, [new(doc, "中止.txt")], new CancellationToken(true)).Single();
        Require(aborted.Error == "キャンセル" && !Directory.EnumerateFiles(destination, "*.part", SearchOption.AllDirectories).Any()
            && !File.Exists(Path.Combine(destination, "中止.txt")), "中止後に途中ファイルや完成品を残さない");
    }

    private static void Require(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label);
    }
}
#endif
