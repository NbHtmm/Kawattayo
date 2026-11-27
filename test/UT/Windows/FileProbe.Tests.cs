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
}
