namespace ShareFileWatch;

internal sealed class AppOwnedFiles(string file, string database)
{
    private readonly HashSet<string> __internalFiles = InternalFiles(file, database);

    private static HashSet<string> InternalFiles(string file, string db)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { file, file + ".bak", file + ".tmp" };
        foreach (var suffix in new[] { "", "-journal", "-wal", "-shm" }) paths.Add(db + suffix);
        // 旧版を同じフォルダに置いたままでも、そのキャッシュを通常の変更として通知しない。
        if (Path.GetFileName(file) == AppDefaults.SettingsFileName)
        {
            var old = Path.Combine(Path.GetDirectoryName(file)!, "cache.json");
            foreach (var suffix in new[] { "", ".bak", ".tmp" }) paths.Add(old + suffix);
        }
        return paths;
    }

    internal bool Contains(string path)
    {
        var name = Path.GetFullPath(path);
        if (__internalFiles.Contains(name)) return true;
        string prefix = file + ".broken-";
        return name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            Guid.TryParseExact(name.AsSpan(prefix.Length), "N", out _);
    }
}
