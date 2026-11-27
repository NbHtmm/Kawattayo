namespace ShareFileWatch;

// GUI と CLI は、呼び出し時のカレントディレクトリではなく同じ配布先を渡す。
internal sealed record WindowsAppPaths(string Directory)
{
    internal string Settings => Path.Combine(Directory, AppDefaults.SettingsFileName);
    internal string Cache => Path.Combine(Directory, "cache.sqlite3");

    internal Checkpoints CreateCheckpoints() =>
        new(new SqliteCheckpointRepository(Cache), new WindowsCheckpointScanner(), new LocalDownloads(), TimeProvider.System);
}
