using ShareFileWatch;
using Xunit;

public sealed class LocalDownloadsTests
{
    [Theory]
    [InlineData("report.TXT", true)]
    [InlineData("sub/report.PdF", true)]
    [InlineData("report.xlsx", true)]
    [InlineData("report.txt.exe", false)]
    [InlineData("report.lnk", false)]
    [InlineData("README", false)]
    public void CanOpen_UsesFinalExtensionAndOnlyAllowsSupportedDocuments(string path, bool expected) =>
        Assert.Equal(expected, LocalDownloads.CanOpen(path));
}
