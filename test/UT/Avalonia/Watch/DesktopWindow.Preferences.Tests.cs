using ShareFileWatch;
using Xunit;

public sealed class DesktopWindowPreferencesTests
{
    [Fact]
    public void AppendOfficeExclusion_HandlesMissingEmptyAndWhitespaceInput()
    {
        Assert.Equal("~$*", DesktopWindow.AppendOfficeExclusion(null));
        Assert.Equal("~$*", DesktopWindow.AppendOfficeExclusion(""));
        Assert.Equal("~$*", DesktopWindow.AppendOfficeExclusion(" \r\n\t "));
    }

    [Fact]
    public void AppendOfficeExclusion_PreservesExistingRulesAndRecognizesOnlyAnExactExistingLine()
    {
        var updated = DesktopWindow.AppendOfficeExclusion("*.tmp\r\n~$*.docx \r\n ");
        Assert.Equal("*.tmp\r\n~$*.docx" + Environment.NewLine + "~$*", updated);
        Assert.Null(DesktopWindow.AppendOfficeExclusion(updated));
        Assert.Null(DesktopWindow.AppendOfficeExclusion("  ~$* \r\n*.txt \r\n\r\n"));
        Assert.Null(DesktopWindow.AppendOfficeExclusion("*.tmp\n  ~$*\nlog?.txt"));
    }
}
