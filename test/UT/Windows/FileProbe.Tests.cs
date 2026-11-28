using Xunit;

namespace ShareFileWatch;

public sealed class FileProbeTests
{
    [Theory]
    [InlineData(@"  ""C:/資料 2026/計測.txt""  ", @"C:\資料 2026\計測.txt")]
    [InlineData(@"\\fixture\sample\資料.txt", @"\\fixture\sample\資料.txt")]
    public void Normalize_PreservesTheAbsoluteTarget(string input, string expected) =>
        Assert.Equal(expected, FileProbe.Normalize(input));

    [Theory]
    [InlineData(@"C:relative.txt")]
    [InlineData(@"relative\file.txt")]
    [InlineData(@"C:\fixture\*.txt")]
    [InlineData(@"C:\fixture\file.txt\")]
    public void Normalize_RejectsNonFileSpecifications(string input) =>
        Assert.Throws<ArgumentException>(() => FileProbe.Normalize(input));

    [Theory]
    [InlineData(@"C:/fixture/", @"C:\fixture")]
    [InlineData(@"\\fixture\sample\folder\", @"\\fixture\sample\folder")]
    public void NormalizeFolder_PreservesTheAbsoluteRoot(string input, string expected) =>
        Assert.Equal(expected, FileProbe.NormalizeFolder(input));

    [Theory]
    [InlineData(@"relative\folder")]
    [InlineData(@"\\fixture")]
    [InlineData(@"C:\folder\child*")]
    public void NormalizeFolder_RejectsNonFolderSpecifications(string input) =>
        Assert.Throws<ArgumentException>(() => FileProbe.NormalizeFolder(input));

    [Fact]
    public void ExtendedPath_LeavesBothPathTypesUnchangedBelowTheWin32Threshold()
    {
        Assert.Equal(@"C:\test\普通.txt", FileProbe.ExtendedPath(@"C:\test\普通.txt"));
        Assert.Equal(@"\\fixture\share\普通.txt", FileProbe.ExtendedPath(@"\\fixture\share\普通.txt"));
        string near = @"C:\test\".PadRight(247, 'x');
        Assert.Equal(near, FileProbe.ExtendedPath(near));
    }

    [Fact]
    public void ExtendedPath_PreservesUnicodeAndUsesTheCorrectPrefixAtAndAboveTheThreshold()
    {
        string local = @"C:\試験\".PadRight(248, 'x');
        string share = @"\\fixture\share\試験\".PadRight(249, 'x');
        Assert.Equal(@"\\?\" + local, FileProbe.ExtendedPath(local));
        Assert.Equal(@"\\?\UNC\" + share[2..], FileProbe.ExtendedPath(share));
    }

    [Fact]
    public void ToLong_CombinesBothWordsWithoutSignExtendingTheLowWord()
    {
        Assert.Equal(0L, FileProbe.ToLong(new()));
        Assert.Equal(0x00000000ffffffffL, FileProbe.ToLong(new() { dwLowDateTime = -1 }));
        Assert.Equal(0x123456789abcdef0L, FileProbe.ToLong(new() { dwHighDateTime = 0x12345678, dwLowDateTime = unchecked((int)0x9abcdef0) }));
        Assert.Equal(long.MinValue, FileProbe.ToLong(new() { dwHighDateTime = int.MinValue }));
        Assert.Equal(-1L, FileProbe.ToLong(new() { dwHighDateTime = -1, dwLowDateTime = -1 }));
    }

}
