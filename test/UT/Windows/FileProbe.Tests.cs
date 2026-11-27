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
    public void Read_DistinguishesAnAbsentFileFromAnUnavailableParent()
    {
        var root = Directory.CreateTempSubdirectory("kw-file-probe-").FullName;
        try
        {
            var path = Path.Combine(root, "測定 01.txt");
            var missing = FileProbe.Read(path);
            Assert.Equal(ProbeState.Missing, missing.State);
            Assert.Null(missing.Error);
            Assert.Null(missing.Fingerprint);

            var unavailable = FileProbe.Read(Path.Combine(root, "missing parent", "item.txt"));
            Assert.Equal(ProbeState.Unavailable, unavailable.State);
            Assert.NotEmpty(unavailable.Error!);
            Assert.Null(unavailable.Fingerprint);
        }
        finally { Directory.Delete(root); }
    }

    [Fact]
    public void Read_ReportsMetadataEvenWhileFileIsOpenWithoutSharing()
    {
        var root = Directory.CreateTempSubdirectory("kw-file-probe-").FullName;
        var path = Path.Combine(root, "開いているファイル.txt");
        try
        {
            File.WriteAllBytes(path, [1, 2, 3, 4, 5]);
            using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var result = FileProbe.Read(path);
                Assert.Equal(ProbeState.Found, result.State);
                Assert.Equal(5ul, result.Fingerprint!.Bytes);
                Assert.Equal(File.GetLastWriteTimeUtc(path).ToFileTimeUtc(), result.Fingerprint.Modified);
            }

            // Reopening exclusively and then deleting checks the probe retained no file handle.
            using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            File.Delete(path);
            Assert.Equal(ProbeState.Missing, FileProbe.Read(path).State);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            Directory.Delete(root);
        }
    }

    [Fact]
    public void ListDirectory_HandlesEmptyAndLimitsWithoutLeavingAnEnumerationOpen()
    {
        var root = Directory.CreateTempSubdirectory("kw-file-probe-").FullName;
        var file = Path.Combine(root, "known.txt");
        var child = Path.Combine(root, "child");
        var stop = TestContext.Current.CancellationToken;
        try
        {
            Assert.Empty(FileProbe.ListDirectory(root, 0, stop));
            File.WriteAllBytes(file, [1, 2]);
            Directory.CreateDirectory(child);
            var entries = FileProbe.ListDirectory(root, 2, stop);
            Assert.Contains(entries, entry => !entry.IsFolder && entry.Name == "known.txt" && entry.Fingerprint.Bytes == 2);
            Assert.Contains(entries, entry => entry.IsFolder && entry.Name == "child");
            Assert.Throws<IOException>(() => FileProbe.ListDirectory(root, 1, stop));
            Assert.Throws<OperationCanceledException>(() => FileProbe.ListDirectory(root, 2, new(true)));

            Directory.Delete(child);
            File.Delete(file);
            Assert.Empty(FileProbe.ListDirectory(root, 1, stop));
        }
        finally
        {
            if (Directory.Exists(child)) Directory.Delete(child);
            if (File.Exists(file)) File.Delete(file);
            Directory.Delete(root);
        }
    }
}
