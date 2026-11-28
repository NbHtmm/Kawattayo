using Xunit;

namespace ShareFileWatch;

public sealed class DesktopIdentityTests
{
    [Fact]
    public void WindowTitle_ReportsTheRunningGuiAssemblyVersion()
    {
        var assembly = typeof(DesktopIdentity).Assembly.GetName();
        var version = Assert.IsType<Version>(assembly.Version);
        Assert.Equal("Kawattayo.Avalonia", assembly.Name);
        Assert.Equal($"{AppDefaults.AppName} v{version.Major}.{version.Minor}.{version.Build}", DesktopIdentity.WindowTitle);
    }
}
