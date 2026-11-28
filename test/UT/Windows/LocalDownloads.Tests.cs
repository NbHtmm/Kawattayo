using ShareFileWatch;
using Xunit;

public sealed class LocalDownloadsTests
{
    [Theory]
    [InlineData("report.TXT", true)]
    [InlineData("sub/report.PdF", true)]
    [InlineData("report.xlsx", true)]
    [InlineData("document.log", true)]
    [InlineData("document.csv", true)]
    [InlineData("document.tsv", true)]
    [InlineData("document.png", true)]
    [InlineData("document.jpg", true)]
    [InlineData("document.jpeg", true)]
    [InlineData("document.gif", true)]
    [InlineData("document.webp", true)]
    [InlineData("document.bmp", true)]
    [InlineData("document.docx", true)]
    [InlineData("document.pptx", true)]
    [InlineData("document.odt", true)]
    [InlineData("document.ods", true)]
    [InlineData("document.odp", true)]
    [InlineData("report.txt.exe", false)]
    [InlineData("report.lnk", false)]
    [InlineData("README", false)]
    [InlineData("report.", false)]
    [InlineData("report.doc", false)]
    [InlineData("report.zip", false)]
    [InlineData("report.cmd", false)]
    [InlineData("report.txt.pdf.zip", false)]
    [InlineData("", false)]
    public void CanOpen_UsesFinalExtensionAndOnlyAllowsSupportedDocuments(string path, bool expected) =>
        Assert.Equal(expected, LocalDownloads.CanOpen(path));
}
