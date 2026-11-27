using ShareFileWatch;
using Xunit;

public sealed class WindowsAppPathsTests
{
    [Fact]
    public void SettingsAndCachePaths_AreBothUnderTheSuppliedDistributionFolder()
    {
        string root = Path.GetFullPath(Path.Combine("test-distribution", "日本語と空白"));
        var paths = new WindowsAppPaths(root);
        Assert.Equal(Path.Combine(root, "settings.json"), paths.Settings);
        Assert.Equal(Path.Combine(root, "cache.sqlite3"), paths.Cache);
    }
}
