using Xunit;

namespace ShareFileWatch;

public sealed class UpdateHooksTests
{
    [Fact]
    public void StartInfo_PreservesArgumentBoundariesAndWorkingDirectory()
    {
        var exe = @"C:\test tools\test-helper.exe";
        var path = @"C:\資料\測定 01 & 02.txt";
        var start = UpdateHooks.StartInfo(new(@"C:\資料", exe, ["--output", "日本語 ログ"], 0), path);

        Assert.Equal(exe, start.FileName);
        Assert.Equal(@"C:\test tools", start.WorkingDirectory);
        Assert.False(start.UseShellExecute);
        Assert.True(start.CreateNoWindow);
        Assert.Equal(new[] { "--output", "日本語 ログ", path }, start.ArgumentList);
    }
}
