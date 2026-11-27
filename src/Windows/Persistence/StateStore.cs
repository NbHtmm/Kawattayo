namespace ShareFileWatch;

// 設定と監視キャッシュの保存順だけを管理する。障害時も個々の保存先の形式は変えない。
internal sealed class StateStore(string file, string? database = null)
{
    private readonly SettingsFile settings = new(file);
    private readonly MonitoringCache cache = new(database ?? Path.ChangeExtension(file, ".sqlite3"));
    private readonly AppOwnedFiles owned = new(file, database ?? Path.ChangeExtension(file, ".sqlite3"));

    internal string StorageDirectory => Path.GetDirectoryName(cache.DatabaseFile) ?? AppContext.BaseDirectory;
    internal bool IsInternalFile(string path) => owned.Contains(path);

    internal static StateStore BesideExecutable(string directory)
    {
        var paths = new WindowsAppPaths(directory);
        var store = new StateStore(paths.Settings, paths.Cache);
        store.settings.EnsurePresent();
        return store;
    }

    public (AppState State, string? Warning) Load()
    {
        var (state, warning) = settings.Load();
        // 壊れたキャッシュを空データで上書きしない。呼び出し元に失敗を返す。
        cache.Restore(state);
        return (state, warning);
    }

    public void Save(AppState state)
    {
        settings.Save(state);
        cache.Save(state);
    }
}
