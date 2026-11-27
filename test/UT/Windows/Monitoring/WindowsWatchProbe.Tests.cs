using ShareFileWatch;
using Xunit;

public sealed class WindowsWatchProbeTests
{
    [Theory]
    [InlineData("settings.json")]
    [InlineData("cache.sqlite3-wal")]
    [InlineData("settings.json.broken-0123456789abcdef0123456789abcdef")]
    public void Read_AppOwnedFileIsUnavailableAndKeepsTargetIdentity(string name)
    {
        var root = Path.GetFullPath("test-distribution");
        var owned = new AppOwnedFiles(Path.Combine(root, "settings.json"), Path.Combine(root, "cache.sqlite3"));
        var checkedPaths = new List<string>();
        var probe = new WindowsWatchProbe(path =>
        {
            checkedPaths.Add(path);
            // The app-owned shortcut must not require a real file or any Win32 reads.
            return owned.Contains(path);
        });
        var request = new WatchScanRequest(Guid.NewGuid(), Path.Combine(root, name), false, false, 27, [], false);

        var observation = probe.Read(request, TestContext.Current.CancellationToken);
        Assert.Equal(request.Id, observation.Id);
        Assert.Equal(request.Revision, observation.Revision);
        Assert.Equal(request.Path, Assert.Single(checkedPaths));
        Assert.Null(observation.Folder);
        Assert.Null(observation.PreparedChanges);
        var file = Assert.IsType<ProbeResult>(observation.File);
        Assert.Equal(ProbeState.Unavailable, file.State);
        Assert.Null(file.Fingerprint);
        Assert.False(string.IsNullOrWhiteSpace(file.Error));
    }
}
