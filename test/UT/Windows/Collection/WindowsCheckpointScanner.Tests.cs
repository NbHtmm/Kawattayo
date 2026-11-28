using System.ComponentModel;
using ShareFileWatch;
using Xunit;

public sealed class WindowsCheckpointScannerTests
{
    private const string Root = @"C:\test\checkpoint";
    private static readonly Fingerprint s_content = new(21, 89, 13);
    private static CheckpointInfo Scope(bool recursive = true) => new("id", "trial", Root, recursive, ["*.tmp"], DateTimeOffset.UnixEpoch);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Scan_AppliesRecursionExclusionsAndBudgetToOneTraversal(bool recursive)
    {
        using var stop = new CancellationTokenSource();
        var calls = new List<(string, int)>();
        string childPath = Path.Combine(Root, "child");
        var result = new WindowsCheckpointScanner(path => path == Path.Combine(Root, "owned.dat")).Scan(
            Scope(recursive), stop.Token, _ => FileAttributes.Directory, (path, remaining, token) =>
            {
                Assert.Equal(stop.Token, token);
                calls.Add((path, remaining));
                return path == Root
                    ? [new("child", true, s_content), new("FIRST.dat", false, s_content),
                        new("trash.tmp", false, s_content), new("owned.dat", false, s_content)]
                    : [new("deep.dat", false, s_content)];
            }, 8);
        Assert.Empty(result.Errors);
        Assert.Equal(s_content, result.Files["first.DAT"]);
        Assert.DoesNotContain("trash.tmp", result.Files.Keys);
        Assert.DoesNotContain("owned.dat", result.Files.Keys);
        Assert.Equal(recursive ? new[] { (Root, 8), (childPath, 4) } : [(Root, 8)], calls);
        if (recursive) Assert.Equal(s_content, result.Files[Path.Combine("CHILD", "DEEP.dat")]);
        else Assert.Single(result.Files);
    }

    [Theory]
    [InlineData(FileAttributes.Normal)]
    [InlineData(FileAttributes.Directory | FileAttributes.ReparsePoint)]
    public void Scan_RejectsNonDirectoriesAndReparsePointsWithoutListing(FileAttributes flags)
    {
        var result = new WindowsCheckpointScanner(_ => false).Scan(Scope(), CancellationToken.None, _ => flags,
            (_, _, _) => throw new InvalidOperationException("Rejected roots may not be listed"), 10);
        Assert.Empty(result.Files);
        Assert.False(string.IsNullOrWhiteSpace(Assert.Single(result.Errors).Value));
        Assert.Equal("", Assert.Single(result.Errors).Key);
    }

    [Theory]
    [InlineData("io")]
    [InlineData("access")]
    [InlineData("native")]
    public void Scan_RecordsExpectedChildErrorsAndContinuesWithItsSibling(string kind)
    {
        Exception failure = kind switch { "io" => new IOException("io"), "access" => new UnauthorizedAccessException("access"),
            _ => new Win32Exception(5, "native") };
        var result = new WindowsCheckpointScanner(_ => false).Scan(Scope(), CancellationToken.None,
            _ => FileAttributes.Directory, (path, _, _) => Path.GetFileName(path) switch
            {
                "bad" => throw failure,
                "good" => [new("ok.txt", false, s_content)],
                _ => [new("good", true, s_content), new("bad", true, s_content)]
            }, 5);
        Assert.Equal(failure.Message, result.Errors["BAD"]);
        Assert.Equal(s_content, Assert.Single(result.Files).Value);
    }

    [Fact]
    public void Scan_PassesExhaustedBudgetAndKeepsVerifiedEntriesFromOtherFolders()
    {
        var exhausted = new IOException("listing limit reached");
        var result = new WindowsCheckpointScanner(_ => false).Scan(Scope(), CancellationToken.None,
            _ => FileAttributes.Directory, (path, remaining, _) =>
            {
                if (path == Root)
                {
                    Assert.Equal(2, remaining);
                    return [new("last", true, s_content), new("first.dat", false, s_content)];
                }
                Assert.Equal(0, remaining);
                throw exhausted;
            }, 2);
        Assert.Equal("first.dat", Assert.Single(result.Files).Key);
        Assert.Equal(exhausted.Message, result.Errors["last"]);
    }

    [Fact]
    public void Scan_SkipsTheListingWhenFolderAttributesAreUnavailable()
    {
        var error = new UnauthorizedAccessException("attributes denied");
        var result = new WindowsCheckpointScanner(_ => false).Scan(Scope(), CancellationToken.None, _ => throw error,
            (_, _, _) => throw new InvalidOperationException("Unreadable folders may not be listed"), 2);
        Assert.Empty(result.Files);
        Assert.Equal(error.Message, result.Errors[""]);
    }

    [Fact]
    public void Scan_PropagatesCancellationBeforeAndBetweenFoldersAndUnexpectedExceptions()
    {
        using var stop = new CancellationTokenSource();
        var scanner = new WindowsCheckpointScanner(_ => false);
        Assert.Throws<OperationCanceledException>(() => scanner.Scan(Scope(), new(true),
            _ => throw new InvalidOperationException("No attributes expected"), (_, _, _) => [], 5));
        int calls = 0;
        Assert.Throws<OperationCanceledException>(() => scanner.Scan(Scope(), stop.Token,
            _ => FileAttributes.Directory, (_, _, _) =>
            {
                calls++;
                stop.Cancel();
                return [new("child", true, s_content)];
            }, 5));
        Assert.Equal(1, calls);
        var error = new InvalidOperationException("unexpected");
        Assert.Same(error, Assert.Throws<InvalidOperationException>(() => scanner.Scan(Scope(), CancellationToken.None,
            _ => FileAttributes.Directory, (_, _, _) => throw error, 5)));
    }
}
