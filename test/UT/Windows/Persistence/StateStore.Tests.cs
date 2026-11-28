using Xunit;

namespace ShareFileWatch;

public sealed class StateStoreTests
{
    [Fact]
    public void Load_AppliesTheCacheToTheLoadedStateAndPreservesItsWarning()
    {
        var loaded = new AppState { PollSeconds = 25 };
        var calls = new List<string>();
        var storage = Substitute(
            load: () => { calls.Add("settings"); return (loaded, "recovered"); },
            restore: target =>
            {
                calls.Add("cache");
                Assert.Same(loaded, target);
                target.History.Enqueue(new(DateTimeOffset.UnixEpoch, @"C:\fixture\a.txt", "更新"));
            });
        var (actual, warning) = storage.Load();
        Assert.Equal(new[] { "settings", "cache" }, calls);
        Assert.Same(loaded, actual);
        Assert.Single(actual.History);
        Assert.Equal("recovered", warning);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Load_SurfacesSettingsOrCacheFailureWithoutReturningOrWritingPartialState(bool settingsFailure)
    {
        var failure = new IOException("failed storage");
        var calls = new List<string>();
        var storage = Substitute(
            load: () => { calls.Add("settings"); return settingsFailure ? throw failure : (new(), null); },
            restore: _ => { calls.Add("cache"); throw failure; });
        Assert.Same(failure, Assert.Throws<IOException>(() => storage.Load()));
        Assert.Equal(settingsFailure ? ["settings"] : new[] { "settings", "cache" }, calls);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("settings")]
    [InlineData("cache")]
    public void Save_UsesTheSameSnapshotInOrderAndDoesNotHideWriteErrors(string failurePoint)
    {
        var snapshot = new AppState { PollSeconds = 42 };
        var failure = new IOException("write fixture");
        var calls = new List<string>();
        void Write(string destination, AppState state)
        {
            Assert.Same(snapshot, state);
            calls.Add(destination);
            if (destination == failurePoint) throw failure;
        }
        var storage = Substitute(saveSettings: state => Write("settings", state), saveCache: state => Write("cache", state));
        if (failurePoint == "none") storage.Save(snapshot);
        else Assert.Same(failure, Assert.Throws<IOException>(() => storage.Save(snapshot)));
        Assert.Equal(failurePoint == "settings" ? ["settings"] : new[] { "settings", "cache" }, calls);
    }

    private static StateStore Substitute(Func<(AppState State, string? Warning)>? load = null, Action<AppState>? restore = null,
        Action<AppState>? saveSettings = null, Action<AppState>? saveCache = null) =>
        new(@"C:\fixture\settings.json", backend: new(
            () => throw new InvalidOperationException("Unexpected settings initialization"),
            load ?? (() => throw new InvalidOperationException("Unexpected settings load")),
            restore ?? (_ => throw new InvalidOperationException("Unexpected cache restore")),
            saveSettings ?? (_ => throw new InvalidOperationException("Unexpected settings save")),
            saveCache ?? (_ => throw new InvalidOperationException("Unexpected cache save"))));

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
