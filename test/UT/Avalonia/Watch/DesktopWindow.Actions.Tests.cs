using ShareFileWatch;
using Xunit;

public sealed class DesktopWindowActionsTests
{
    private static readonly DateTimeOffset s_origin = DateTimeOffset.UnixEpoch;

    [Fact]
    public void CaptureAcknowledgement_NoTargetDoesNothingAndUnchangedRootClearsItsUnread()
    {
        DesktopWindow.CaptureAcknowledgement(Row(null, null))();
        var file = new WatchItem { Path = @"C:\trial\a.txt", LastChanged = s_origin, Unread = 1 };
        DesktopWindow.CaptureAcknowledgement(Row(file, file.Path, root: true))();
        Assert.Equal(0, file.Unread);
        Assert.NotNull(file.AcknowledgedAt);
    }

    [Fact]
    public void CaptureAcknowledgement_ChildDoesNotClearItsNewerReplacementOrAnySibling()
    {
        var folder = Folder();
        var requested = Add(folder, @"logs\one.txt");
        var other = Add(folder, @"logs\two.txt");
        var ack = DesktopWindow.CaptureAcknowledgement(Row(folder, Path.Combine(folder.Path, requested.Relative), requested));
        var newer = requested with { DetectedAt = s_origin.AddSeconds(1) };
        folder.PendingFiles[requested.Relative] = newer;
        ack();
        Assert.Equal(2, folder.Unread);
        Assert.Same(newer, folder.PendingFiles[requested.Relative]);
        DesktopWindow.CaptureAcknowledgement(Row(folder, Path.Combine(folder.Path, requested.Relative), newer))();
        Assert.Equal(1, folder.Unread);
        Assert.Same(other, Assert.Single(folder.PendingFiles).Value);
    }

    [Fact]
    public void CaptureAcknowledgement_FolderScopeUsesAPathBoundaryAndOnlyClearsCapturedEntries()
    {
        var folder = Folder();
        var captured = Add(folder, @"Logs\first.txt");
        var sibling = Add(folder, @"logstash\stay.txt");
        DesktopWindow.CaptureAcknowledgement(Row(folder, null))();
        Assert.Equal(2, folder.Unread);
        var ack = DesktopWindow.CaptureAcknowledgement(Row(folder, Path.Combine(folder.Path, "logs") + "\\"));
        var arrivedLater = Add(folder, @"logs\later.txt");
        ack();
        Assert.Equal(2, folder.Unread);
        Assert.False(folder.PendingFiles.ContainsKey(captured.Relative));
        Assert.Same(sibling, folder.PendingFiles[sibling.Relative]);
        Assert.Same(arrivedLater, folder.PendingFiles[arrivedLater.Relative]);
    }

    [Fact]
    public void CaptureAcknowledgement_RootWithNewUpdatesClearsOlderSnapshotOnly()
    {
        var folder = Folder();
        var before = Add(folder, "first.txt");
        var ack = DesktopWindow.CaptureAcknowledgement(Row(folder, folder.Path, root: true));
        var after = Add(folder, "second.txt", s_origin.AddSeconds(1));
        ack();
        Assert.Equal(1, folder.Unread);
        Assert.False(folder.PendingFiles.ContainsKey(before.Relative));
        Assert.Same(after, Assert.Single(folder.PendingFiles).Value);
    }

    private static WatchItem Folder() => new() { Path = @"C:\trial", IsFolder = true };

    private static FileChange Add(WatchItem target, string relative, DateTimeOffset? time = null)
    {
        var change = new FileChange(relative, "更新", time ?? s_origin);
        target.PendingFiles.Add(relative, change);
        target.Unread++;
        target.LastChanged = change.DetectedAt;
        return change;
    }

    private static DesktopWindow.Row Row(WatchItem? target, string? path, FileChange? change = null, bool root = false) =>
        new("key", target, "name", "", null, Path: path, Change: change, Root: root);
}
