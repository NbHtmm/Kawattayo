namespace ShareFileWatch;

// GUI と CLI は、呼び出し時のカレントディレクトリではなく同じ配布先を渡す。
internal sealed record WindowsAppPaths(string Directory)
{
    internal string Settings => Path.Combine(Directory, AppDefaults.SettingsFileName);
    internal string Cache => Path.Combine(Directory, "cache.sqlite3");

    internal Checkpoints CreateCheckpoints()
    {
        // 監視と差分収集で、比較DBや設定を「利用者の更新」として数えない条件を共有する。
        var owned = new AppOwnedFiles(Settings, Cache);
        return new(new SqliteCheckpointRepository(Cache, owned.Contains), new WindowsCheckpointScanner(owned.Contains),
            new LocalDownloads(), TimeProvider.System);
    }
}
