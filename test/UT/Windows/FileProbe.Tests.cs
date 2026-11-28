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
    public void Normalize_ValidatesFilePathsAndPreservesTheSelectedName()
    {
        Assert.Equal(@"C:\fixtures\quarterly.report.txt",
            FileProbe.Normalize(@"  ""C:/fixtures/quarterly.report.txt""  "));
        Assert.Equal(@"\\fixture\share\directory\report.txt",
            FileProbe.Normalize(@"\\fixture\share\directory\report.txt"));

        // ファイル指定にはルートだけ、曖昧な名前、ファイル以外のパス形式を認めない。
        string[] rejected =
        [
            "", "   ", @"C:report.txt", @"\report.txt", @"report.txt",
            @"C:\", @"\\fixture\share\", @"C:\fixtures\report.txt\", @"C:\fixtures\\report.txt",
            @"C:\fixtures\sample.\report.txt", @"C:\fixtures\sample \report.txt",
            @"C:\fixtures\report.", @"C:\fixtures\invalid:name.txt",
            @"C:\fixtures\*.txt", @"C:\fixtures\report?.txt", @"C:\fixtures\rep""ort.txt",
            @"C:\fixtures\rep<ort.txt", @"C:\fixtures\rep>ort.txt", @"C:\fixtures\rep|ort.txt",
            "C:\\fixtures\\re\0port.txt", @"\\?\C:\fixtures\report.txt", @"\\.\C:\fixtures\report.txt"
        ];
        foreach (var path in rejected)
        {
            Assert.Throws<ArgumentException>(() => FileProbe.Normalize(path));
        }
    }

    [Fact]
    public void NormalizeFolder_ValidatesFolderPathsAndAllowsRealRoots()
    {
        Assert.Equal(@"C:\", FileProbe.NormalizeFolder(@"C:\"));
        Assert.Equal(@"\\fixture\share", FileProbe.NormalizeFolder(@"\\fixture\share\"));
        Assert.Equal(@"\\fixture\share", FileProbe.NormalizeFolder(@"\\fixture\share"));
        Assert.Equal(@"C:\fixtures\test folder",
            FileProbe.NormalizeFolder(@" ""C:/fixtures/test folder/"" "));
        Assert.Equal(@"C:\fixtures\test folder",
            FileProbe.NormalizeFolder(@"C:\fixtures\previous\..\test folder"));

        // サーバー名だけは共有のルートではない。ドライブや共有のルートとは区別する。
        string[] rejected =
        [
            "", " ", @"C:folder", @"\folder", @"relative\folder",
            @"\\fixture", @"\\fixture\", @"C:\fixtures\invalid:name",
            @"C:\fixtures\*", @"C:\fixtures\?", @"C:\fixtures\fo""lder",
            @"C:\fixtures\fo<lder", @"C:\fixtures\fo>lder", @"C:\fixtures\fo|lder",
            "C:\\fixtures\\fo\0lder", @"\\?\C:\fixtures", @"\\.\C:\fixtures"
        ];
        foreach (var path in rejected)
        {
            Assert.Throws<ArgumentException>(() => FileProbe.NormalizeFolder(path));
        }
    }

    [Fact]
    public void Read_ReportsRegularFileMetadataAndRejectsOtherEntryTypes()
    {
        const string path = @"C:\fixture\report.txt";
        var record = new FileProbe.FindData
        {
            Created = new() { dwHighDateTime = 1, dwLowDateTime = 2 },
            Written = new() { dwHighDateTime = 3, dwLowDateTime = 4 },
            SizeHigh = 1,
            SizeLow = 5
        };
        var read = FileProbe.Read(path, Find, UnexpectedDirectoryQuery);
        Assert.Equal(new ProbeResult(ProbeState.Found, new((3L << 32) | 4, (1L << 32) | 2, (1UL << 32) | 5)), read);
        foreach (uint flags in new uint[] { 0x10, 0x400, 0x410 })
        {
            record.Attributes = flags;
            var rejected = FileProbe.Read(path, Find, UnexpectedDirectoryQuery);
            Assert.Equal(ProbeState.Unavailable, rejected.State);
            Assert.Null(rejected.Fingerprint);
            Assert.NotEmpty(rejected.Error!);
        }

        bool Find(string candidate, out FileProbe.FindData data, out int error)
        {
            Assert.Equal(path, candidate);
            data = record;
            error = 0;
            return true;
        }
    }

    [Fact]
    public void Read_DistinguishesMissingFilesFromFailedParentProbes()
    {
        const string parent = @"C:\fixture";
        string path = Path.Combine(parent, "report.txt");
        int errorCode = 2;
        bool canEnumerateParent = true;
        int parentError = 0;
        bool exists = false;
        var queries = new List<string>();
        Assert.Equal(new ProbeResult(ProbeState.Missing), FileProbe.Read(path, Find, UnexpectedDirectoryQuery));
        Assert.Equal(new[] { path, Path.Combine(parent, "*") }, queries);

        canEnumerateParent = false;
        foreach (int emptyCode in new[] { 2, 18 })
        {
            parentError = emptyCode;
            exists = true;
            Assert.Equal(new ProbeResult(ProbeState.Missing), FileProbe.Read(path, Find, DirectoryExists));
            exists = false;
            Assert.Equal(ProbeState.Unavailable, FileProbe.Read(path, Find, DirectoryExists).State);
        }
        parentError = 5;
        Assert.Equal(ProbeState.Unavailable, FileProbe.Read(path, Find, UnexpectedDirectoryQuery).State);

        // アクセス拒否やネットワークエラー自体をファイルの削除として扱わない。
        foreach (int directError in new[] { 3, 5, 53 })
        {
            queries.Clear();
            errorCode = directError;
            var result = FileProbe.Read(path, Find, UnexpectedDirectoryQuery);
            Assert.Equal(ProbeState.Unavailable, result.State);
            Assert.Contains($"Win32 {directError}", result.Error);
            Assert.Equal(path, Assert.Single(queries));
        }
        Assert.Equal(ProbeState.Unavailable, FileProbe.Read(@"C:\", RootMissing, UnexpectedDirectoryQuery).State);

        bool Find(string candidate, out FileProbe.FindData data, out int error)
        {
            queries.Add(candidate);
            data = default;
            if (candidate == path)
            {
                error = errorCode;
                return false;
            }
            Assert.Equal(Path.Combine(parent, "*"), candidate);
            error = parentError;
            return canEnumerateParent;
        }

        bool DirectoryExists(string candidate)
        {
            Assert.Equal(parent, candidate);
            return exists;
        }
    }

    private static bool RootMissing(string candidate, out FileProbe.FindData data, out int error)
    {
        Assert.Equal(@"C:\", candidate);
        data = default;
        error = 2;
        return false;
    }

    private static bool UnexpectedDirectoryQuery(string path) =>
        throw new InvalidOperationException($"Unexpected directory existence query: {path}");

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
