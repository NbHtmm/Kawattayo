using ShareFileWatch;
using Xunit;

public sealed class DesktopWindowNotificationsTests
{
    [Fact]
    public void FindNotificationTarget_MatchesUnreadDirectAndNestedPathsIgnoringCase()
    {
        const string path = @"C:\trial\Logs\Report.txt";
        var direct = new WatchItem { Path = path, Unread = 1 };
        var folder = new WatchItem { Path = @"C:\trial", Unread = 1, IsFolder = true };
        folder.PendingFiles.Add(@"logs\report.txt", new(@"logs\report.txt", "更新", DateTimeOffset.UnixEpoch));
        var different = new WatchItem { Path = @"C:\elsewhere\Report.txt", Unread = 1 };
        Assert.Same(direct, DesktopWindow.FindNotificationTarget([different, direct, folder], path.ToUpperInvariant()));
        direct.Unread = 0;
        Assert.Same(folder, DesktopWindow.FindNotificationTarget([different, direct, folder], path.ToUpperInvariant()));
        Assert.Same(folder, DesktopWindow.FindNotificationTarget([different, folder], @"c:\TRIAL"));
    }

    [Fact]
    public void FindNotificationTarget_DoesNotMatchReadTargetsOrAnotherFileWithTheSameName()
    {
        var folder = new WatchItem { Path = @"C:\trial", Unread = 1, IsFolder = true };
        folder.PendingFiles.Add(@"logs\report.txt", new(@"logs\report.txt", "更新", DateTimeOffset.UnixEpoch));
        Assert.Null(DesktopWindow.FindNotificationTarget([folder], @"C:\trial\another\report.txt"));
        Assert.Null(DesktopWindow.FindNotificationTarget([folder], @"D:\trial\logs\report.txt"));
        folder.Unread = 0;
        Assert.Null(DesktopWindow.FindNotificationTarget([folder], @"C:\trial\logs\report.txt"));
        Assert.Null(DesktopWindow.FindNotificationTarget([], folder.Path));
    }

    [Fact]
    public void NotificationBranches_OnlyExpandsAncestorsForDirectAndDeepChildren()
    {
        var folder = new WatchItem { Path = @"C:\trial", IsFolder = true };
        var root = folder.Id.ToString();
        Assert.Empty(DesktopWindow.NotificationBranches(folder, @"c:\TRIAL"));
        Assert.Equal(new[] { root }, DesktopWindow.NotificationBranches(folder, @"C:\trial\report.txt"));
        Assert.Equal(new[] { root, root + @"|logs\round1\", root + @"|logs\" },
            DesktopWindow.NotificationBranches(folder, @"C:\trial\logs\round1\report.txt"));
        Assert.Empty(DesktopWindow.NotificationBranches(new() { Path = @"C:\trial\report.txt" }, @"C:\trial\report.txt"));
    }

    [Fact]
    public void FindNotificationRow_UsesBothPathAndRegistrationIdRatherThanFileNameOrArrayPosition()
    {
        const string path = @"C:\trial\report.txt";
        var target = new WatchItem { Path = path };
        var wanted = new DesktopWindow.Row("desired", target, "report.txt", "", null, Path: path);
        DesktopWindow.Row[] alternatives =
        [
            wanted with { Key = "wrong", Target = new() { Path = path } },
            wanted with { Key = "no-target", Target = null },
            wanted with { Key = "no-path", Path = null },
            wanted with { Key = "same-name", Path = @"D:\different\report.txt" }
        ];
        Assert.Null(DesktopWindow.FindNotificationRow(alternatives, target.Id, path));
        Assert.Same(wanted, DesktopWindow.FindNotificationRow([.. alternatives, wanted], target.Id, path.ToUpperInvariant()));
    }
}
