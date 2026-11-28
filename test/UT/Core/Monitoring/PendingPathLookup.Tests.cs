using ShareFileWatch;
using Xunit;

public sealed class PendingPathLookupTests
{
    [Fact]
    public void Contains_FindsNestedUnreadInRegisteredAncestor()
    {
        string root = Path.GetFullPath("watched");
        string relative = Path.Combine("nested", "report.txt");
        var folder = new WatchItem { Path = root + Path.DirectorySeparatorChar, IsFolder = true };
        folder.PendingFiles[relative] = Change(relative);
        var lookup = new PendingPathLookup([folder]);

        Assert.True(lookup.Contains(Path.Combine(root, relative.ToUpperInvariant())));
        Assert.False(lookup.Contains(Path.Combine(root, "other.txt")));
        Assert.False(lookup.Contains(Path.Combine(root + "-other", relative)));
        Assert.False(lookup.Contains(root));
    }

    [Fact]
    public void Contains_RecognizesFolderItselfAndIndividualFileOnlyWhileUnread()
    {
        string root = Path.GetFullPath("watched");
        var folder = new WatchItem { Path = root, IsFolder = true };
        var file = new WatchItem { Path = Path.GetFullPath("single.txt"), Unread = 1 };
        var lookup = new PendingPathLookup([folder, file]);

        folder.PendingFiles["."] = Change(".");
        Assert.True(lookup.Contains(root + Path.DirectorySeparatorChar));
        Assert.True(lookup.Contains(file.Path));
        folder.PendingFiles.Clear();
        file.Unread = 0;
        Assert.False(lookup.Contains(root));
        Assert.False(lookup.Contains(file.Path));
    }

    private static FileChange Change(string path) => new(path, "更新", DateTimeOffset.UnixEpoch);

    [Fact]
    public void DuplicatePathKeepsTheFirstRegistrationAndReadFileCanFallBackToFolder()
    {
        string root = Path.GetFullPath("watched");
        string path = Path.Combine(root, "report.txt");
        var file = new WatchItem { Path = path, Unread = 0 };
        var duplicate = new WatchItem { Path = path.ToUpperInvariant(), Unread = 1 };
        var folder = new WatchItem { Path = root, IsFolder = true };
        var lookup = new PendingPathLookup([file, duplicate, folder]);
        Assert.False(new PendingPathLookup([]).Contains(path));
        Assert.False(lookup.Contains(path));

        folder.PendingFiles["report.txt"] = Change("report.txt");
        Assert.True(lookup.Contains(path));

        // A path registered as a file cannot serve as an ancestor folder.
        var fileAncestor = new WatchItem { Path = root };
        fileAncestor.PendingFiles["report.txt"] = Change("report.txt");
        Assert.False(new PendingPathLookup([fileAncestor]).Contains(path));
    }
}
