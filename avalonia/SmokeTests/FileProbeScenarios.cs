#if KAWATTAYO_SMOKE
namespace ShareFileWatch;

internal static class FileProbeScenarios
{
    internal static void Run(string fixture)
    {
        var root = Directory.CreateDirectory(Path.Combine(fixture, "file-probe-" + Guid.NewGuid().ToString("N"))).FullName;
        var path = Path.Combine(root, "測定 01.txt");
        var child = Path.Combine(root, "child");
        Check(FileProbe.Read(path) is { State: ProbeState.Missing, Error: null, Fingerprint: null },
            "An absent file with a reachable parent must be reported as missing.");
        var unavailable = FileProbe.Read(Path.Combine(root, "absent-parent", "item.txt"));
        Check(unavailable.State == ProbeState.Unavailable && !string.IsNullOrEmpty(unavailable.Error) &&
            unavailable.Fingerprint is null, "An inaccessible parent must not be reported as file deletion.");
        Check(FileProbe.ListDirectory(root, 0).Count == 0, "Empty directories may be read with a zero entry limit.");
        File.WriteAllBytes(path, [1, 2, 3, 4, 5]);
        long written = File.GetLastWriteTimeUtc(path).ToFileTimeUtc();
        using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var result = FileProbe.Read(path);
            Check(result is { State: ProbeState.Found, Fingerprint.Bytes: 5 } && result.Fingerprint.Modified == written,
                "Metadata is available even when the fixture holds the file without sharing.");
        }
        Directory.CreateDirectory(child);
        var entries = FileProbe.ListDirectory(root, 2);
        Check(entries.Count == 2 && entries.Any(x => x.Name == "測定 01.txt" && !x.IsFolder && x.Fingerprint.Bytes == 5) &&
            entries.Any(x => x.Name == "child" && x.IsFolder), "The listing must preserve names, types and size.");
        MustThrow<IOException>(() => FileProbe.ListDirectory(root, 1));
        MustThrow<OperationCanceledException>(() => FileProbe.ListDirectory(root, 2, new(true)));

        // These operations also fail if the probe or enumerator left a conflicting handle open.
        using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        Directory.Delete(child);
        File.Delete(path);
        Check(FileProbe.Read(path).State == ProbeState.Missing, "The deleted file must be reported as absent.");
        Directory.Delete(root);
    }

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
    }

    private static void MustThrow<T>(Action operation) where T : Exception
    {
        try { operation(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name} from the file probe.");
    }
}
#endif
