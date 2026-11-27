using ShareFileWatch;
using Xunit;

public sealed class FileExclusionsTests
{
    [Fact]
    public void Parse_TrimsMixedNewlinesAndRemovesCaseInsensitiveDuplicates() =>
        Assert.Equal(new[] { "~$*", "*.TMP" }, FileExclusions.Parse("  ~$*\r\n\n *.TMP \r*.tmp\n  "));

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("folder/file.txt")]
    public void Parse_RejectsNonFileNames(string invalid) =>
        Assert.Contains(invalid, Assert.Throws<ArgumentException>(() => FileExclusions.Parse("*.tmp\n" + invalid)).Message);

    [Fact]
    public void Normalize_DropsInvalidPatternsAndHandlesMissingList()
    {
        Assert.Empty(FileExclusions.Normalize(null));
        Assert.Equal(new[] { "a?.txt" }, FileExclusions.Normalize(["", "..", " a?.txt ", "A?.TXT"]));
    }

    [Fact]
    public void Same_IgnoresOrderAndCaseButNotDifferentPatterns()
    {
        Assert.True(FileExclusions.Same(null, []));
        Assert.True(FileExclusions.Same(["a*", "b*"], ["B*", "A*"]));
        Assert.False(FileExclusions.Same(["a*", "b*"], ["a*", "c*"]));
        Assert.False(FileExclusions.Same(["a*"], ["a*", "b*"]));
    }

    [Theory]
    [InlineData("folder/REPORT.TMP", "*.tmp", true)]
    [InlineData("folder.tmp/report.txt", "*.tmp", false)]
    [InlineData("folder/a1.txt", "a?.txt", true)]
    [InlineData("folder/a12.txt", "a?.txt", false)]
    public void Matches_ChecksOnlyFileName(string path, string pattern, bool expected) =>
        Assert.Equal(expected, FileExclusions.Matches(path, [pattern]));

    [Fact]
    public void Matches_NoPatternsDoesNotExcludeFile() =>
        Assert.False(FileExclusions.Matches("folder/report.txt", null));
}
