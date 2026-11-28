using Xunit;

namespace ShareFileWatch;

public sealed class StateStoreTests
{
    [Fact]
    public void StorageDirectory_DefaultsBesideSettingsAndCanUseADifferentCacheDirectory()
    {
        string settings = Path.GetFullPath(Path.Combine("fixtures", "settings", "app.json"));
        string cache = Path.GetFullPath(Path.Combine("fixtures", "storage", "watch.sqlite3"));
        Assert.Equal(Path.GetDirectoryName(settings), new StateStore(settings).StorageDirectory);
        Assert.Equal(Path.GetDirectoryName(cache), new StateStore(settings, cache).StorageDirectory);
        Assert.Equal("", new StateStore("app.json", "local.sqlite3").StorageDirectory);
    }

    [Fact]
    public void IsInternalFile_RecognizesOwnedSettingsAndCacheButNotMonitoredFiles()
    {
        string directory = Path.GetFullPath("fixtures");
        string settings = Path.Combine(directory, AppDefaults.SettingsFileName);
        var store = new StateStore(settings);
        string database = Path.ChangeExtension(settings, ".sqlite3");

        // 設定やSQLiteの補助ファイルは監視結果に含めない。似た名前でも別のファイルは含める。
        foreach (var owned in new[] { settings, settings + ".tmp", settings + ".bak", database,
            database + "-journal", database + "-wal", database + "-shm", Path.Combine(directory, "cache.json"),
            settings + ".broken-" + Guid.Empty.ToString("N") })
        {
            Assert.True(store.IsInternalFile(owned));
        }
        Assert.False(store.IsInternalFile(Path.Combine(directory, "日報.txt")));
        Assert.False(store.IsInternalFile(database + ".backup"));
        Assert.False(store.IsInternalFile(settings + ".broken-not-a-guid"));
        Assert.False(store.IsInternalFile(Path.Combine(directory, "other", AppDefaults.SettingsFileName)));

        var custom = new StateStore(settings, Path.Combine(directory, "custom.sqlite3"));
        Assert.True(custom.IsInternalFile(Path.Combine(directory, "custom.sqlite3-wal")));
        Assert.False(custom.IsInternalFile(database));
    }
}
