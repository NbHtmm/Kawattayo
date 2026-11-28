namespace ShareFileWatch;

// 設定と監視キャッシュの保存順だけを管理する。障害時も個々の保存先の形式は変えない。
internal sealed class StateStore(string file, string? database = null, StateStore.Backend? backend = null)
{
    private readonly string cacheFile = database ?? Path.ChangeExtension(file, ".sqlite3");
    private readonly Backend persistence = backend ?? CreateBackend(file, database ?? Path.ChangeExtension(file, ".sqlite3"));
    private readonly AppOwnedFiles owned = new(file, database ?? Path.ChangeExtension(file, ".sqlite3"));

    // The same ordering and failure behavior applies whether persistence is native or in-memory for UT.
    internal sealed record Backend(Action EnsureSettings, Func<(AppState State, string? Warning)> LoadSettings,
        Action<AppState> RestoreCache, Action<AppState> SaveSettings, Action<AppState> SaveCache);

    private static Backend CreateBackend(string file, string database)
    {
        var settings = new SettingsFile(file);
        var cache = new MonitoringCache(database);
        return new(settings.EnsurePresent, settings.Load, cache.Restore, settings.Save, cache.Save);
    }

    internal string StorageDirectory => Path.GetDirectoryName(cacheFile) ?? AppContext.BaseDirectory;
    internal bool IsInternalFile(string path) => owned.Contains(path);

    internal static StateStore BesideExecutable(string directory)
    {
        var paths = new WindowsAppPaths(directory);
        var store = new StateStore(paths.Settings, paths.Cache);
        store.persistence.EnsureSettings();
        return store;
    }

    public (AppState State, string? Warning) Load()
    {
        var (state, warning) = persistence.LoadSettings();
        // 壊れたキャッシュを空データで上書きしない。呼び出し元に失敗を返す。
        persistence.RestoreCache(state);
        return (state, warning);
    }

    public void Save(AppState state)
    {
        persistence.SaveSettings(state);
        persistence.SaveCache(state);
    }
}
