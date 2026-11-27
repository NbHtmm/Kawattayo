using ShareFileWatch;
using Xunit;

public sealed class AppOwnedFilesTests
{
    [Theory]
    [InlineData("SETTINGS.JSON")]
    [InlineData("settings.json.bak")]
    [InlineData("settings.json.tmp")]
    [InlineData("cache.sqlite3")]
    [InlineData("cache.sqlite3-journal")]
    [InlineData("cache.sqlite3-WAL")]
    [InlineData("cache.sqlite3-shm")]
    [InlineData("cache.json")]
    [InlineData("cache.json.bak")]
    [InlineData("cache.json.tmp")]
    [InlineData("settings.json.broken-0123456789abcdef0123456789abcdef")]
    public void Contains_MatchesActualAppArtifacts(string filename)
    {
        var root = Path.GetFullPath("test-distribution");
        var files = new AppOwnedFiles(Path.Combine(root, "settings.json"), Path.Combine(root, "cache.sqlite3"));
        Assert.True(files.Contains(Path.Combine(root, filename)));
    }

    [Theory]
    [InlineData("settings.json.broken-not-a-guid")]
    [InlineData("settings.json.broken-0123456789abcdef0123456789abcdef.txt")]
    [InlineData("cache.sqlite3.old")]
    [InlineData("another/settings.json")]
    public void Contains_DoesNotHideOrdinaryUserFiles(string filename)
    {
        var root = Path.GetFullPath("test-distribution");
        var files = new AppOwnedFiles(Path.Combine(root, "settings.json"), Path.Combine(root, "cache.sqlite3"));
        Assert.False(files.Contains(Path.Combine(root, filename)));
    }

    [Fact]
    public void Contains_RespectsCustomSettingsAndDoesNotHideLegacyCache()
    {
        var root = Path.GetFullPath("test-distribution");
        var files = new AppOwnedFiles(Path.Combine(root, "custom.json"), Path.Combine(root, "custom.sqlite3"));
        Assert.True(files.Contains(Path.Combine(root, "folder", "..", "custom.sqlite3-shm")));
        Assert.False(files.Contains(Path.Combine(root, "cache.json")));
    }
}
