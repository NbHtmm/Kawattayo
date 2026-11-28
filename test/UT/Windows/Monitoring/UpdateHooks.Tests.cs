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

    [Fact]
    public void StartInfo_AppendsTheFileWithNoOptionsAndPreservesEmptyOrQuotedLiteralOptions()
    {
        string exe = @"C:\tools\fixed reader.exe";
        string path = @"C:\reports\draft (03).txt";
        Assert.Equal(path, Assert.Single(UpdateHooks.StartInfo(new("target", exe, [], 0), path).ArgumentList));
        Assert.Equal(new[] { "", "quote=\"value\"", path },
            UpdateHooks.StartInfo(new("target", exe, ["", "quote=\"value\""], 45), path).ArgumentList);
    }

    [Theory]
    [InlineData(@"tools\report.exe")]
    [InlineData(@"C:report.exe")]
    [InlineData(@"C:\tools\report.cmd")]
    [InlineData(@"\\nas\share\report.exe")]
    [InlineData(@"\\?\C:\tools\report.exe")]
    public void ValidateExecutable_RejectsInvalidPathsBeforeAnyFilesystemLookup(string path)
    {
        Assert.Throws<IOException>(() => UpdateHooks.ValidateExecutable(path,
            _ => throw new InvalidOperationException("Invalid path queried a drive"),
            _ => throw new InvalidOperationException("Invalid path queried a file"),
            _ => throw new InvalidOperationException("Invalid path queried attributes")));
    }

    [Theory]
    [InlineData(DriveType.Network, false)]
    [InlineData(DriveType.Removable, false)]
    [InlineData(DriveType.Unknown, false)]
    [InlineData(DriveType.Fixed, true)]
    public void ValidateExecutable_RejectsNonfixedOrAbsentExecutableBeforeReadingAttributes(DriveType drive, bool checkExists)
    {
        int existsCalls = 0;
        Assert.Throws<IOException>(() => UpdateHooks.ValidateExecutable(@"C:\tools\sample.exe",
            path => { Assert.Equal(@"C:\", path); return drive; }, _ => { existsCalls++; return false; },
            _ => throw new InvalidOperationException("Absent file queried attributes")));
        Assert.Equal(checkExists ? 1 : 0, existsCalls);
    }

    [Theory]
    [InlineData("CMD")]
    [InlineData("powershell")]
    [InlineData("PwSh")]
    [InlineData("wscript")]
    [InlineData("cscript")]
    [InlineData("mshta")]
    [InlineData("bash")]
    [InlineData("sh")]
    [InlineData("wsl")]
    public void ValidateExecutable_RejectsShellAndScriptHostNamesIgnoringCase(string name)
    {
        Assert.Throws<IOException>(() => UpdateHooks.ValidateExecutable(@"C:\tools\" + name + ".exe",
            _ => DriveType.Fixed, _ => true, _ => throw new InvalidOperationException("Blocked shell queried attributes")));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(-1)]
    public void ValidateExecutable_ChecksEveryParentForLinksUntilTheRootOrFirstReparsePoint(int linkAt)
    {
        string path = @"C:\tools\sample.ExE";
        string[] chain = [path, @"C:\tools", @"C:\"];
        var queried = new List<string>();
        void Validate() => UpdateHooks.ValidateExecutable(path, _ => DriveType.Fixed, file =>
        {
            Assert.Equal(path, file);
            return true;
        }, part =>
        {
            queried.Add(part);
            return queried.Count - 1 == linkAt ? FileAttributes.ReparsePoint : FileAttributes.Normal;
        });
        if (linkAt >= 0) Assert.Throws<IOException>(Validate);
        else Validate();
        Assert.Equal(linkAt < 0 ? chain : chain[..(linkAt + 1)], queried);
    }
}
