using ShareFileWatch;
using Xunit;

public sealed class MonitorSessionRegistrationsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Register_NormalizesAndDeduplicatesFilesAndFoldersAndAppliesTheDefaultScope(bool recursive)
    {
        var fixture = new MonitorFixture();
        fixture.State.RecursiveFolders = recursive;
        fixture.DirectoryExists = p => p.Equals(@"C:\fixture\folder", StringComparison.OrdinalIgnoreCase);
        fixture.FileExists = p => p.Equals(@"C:\fixture\report.txt", StringComparison.OrdinalIgnoreCase);
        int changes = 0;
        fixture.Session.RowsChanged += _ => changes++;
        Assert.Empty(await fixture.Session.Register(["\"C:/fixture/folder/\"", @"C:\fixture\report.txt", @"c:\FIXTURE\REPORT.TXT"]));
        Assert.Collection(fixture.State.Items,
            x => { Assert.Equal(@"C:\fixture\folder", x.Path); Assert.True(x.IsFolder); Assert.Equal(recursive, x.Recursive); },
            x => { Assert.Equal(@"C:\fixture\report.txt", x.Path); Assert.False(x.IsFolder); Assert.False(x.Recursive); });
        Assert.Empty(await fixture.Session.Register([@"c:\fixture\folder"]));
        Assert.Equal(1, changes);
        await fixture.Session.Close();
    }

    [Fact]
    public async Task Register_ReportsAtMostFiveErrorsAndNeverAddsTheAppsOwnFiles()
    {
        var fixture = new MonitorFixture();
        fixture.Owned = path => path.EndsWith("settings.json", StringComparison.OrdinalIgnoreCase);
        fixture.FileExists = path => path.EndsWith("settings.json", StringComparison.OrdinalIgnoreCase)
            ? throw new InvalidOperationException("Internal files must be rejected before checking existence")
            : false;
        string[] bad = ["relative.txt", @"C:\fixture\settings.json", @"C:\fixture\missing.txt", @"C:\fixture\four.txt",
            @"C:\fixture\five.txt", @"C:\fixture\six.txt"];
        var rejected = await fixture.Session.Register(bad);
        Assert.Equal(5, rejected.Length);
        Assert.All(rejected.Zip(bad), x => Assert.StartsWith(x.Second + "：", x.First));
        Assert.Contains("自身", rejected[1]);
        Assert.Contains("見つかりません", rejected[2]);
        Assert.Empty(fixture.State.Items);
        await fixture.Session.Close();
    }

    [Theory]
    [InlineData("access")]
    [InlineData("io")]
    [InlineData("argument")]
    [InlineData("unsupported")]
    public async Task Register_ReportsExpectedLookupErrorsWithoutDiscardingAValidRegistration(string kind)
    {
        var fixture = new MonitorFixture();
        Exception error = kind switch { "access" => new UnauthorizedAccessException("access"), "io" => new IOException("io"),
            "argument" => new ArgumentException("argument"), _ => new NotSupportedException("unsupported") };
        string bad = @"C:\fixture\bad";
        string ok = @"C:\fixture\report.txt";
        fixture.DirectoryExists = path => path == bad ? throw error : false;
        fixture.FileExists = path => path == ok;
        Assert.Equal(bad + "：" + kind, Assert.Single(await fixture.Session.Register([bad, ok])));
        Assert.Equal(ok, Assert.Single(fixture.State.Items).Path);
        await fixture.Session.Close();
    }

    [Fact]
    public async Task Register_DropsResultsThatArriveAfterClosing()
    {
        var fixture = new MonitorFixture();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var resume = new ManualResetEventSlim(false);
        var stop = TestContext.Current.CancellationToken;
        fixture.DirectoryExists = _ =>
        {
            started.SetResult();
            Assert.True(resume.Wait(TimeSpan.FromSeconds(5), stop), "Registration worker was not released");
            return true;
        };
        var pending = fixture.Session.Register([@"C:\fixture\valid"]);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5), stop);
            await fixture.Session.Close();
        }
        finally { resume.Set(); }
        Assert.Empty(await pending);
        Assert.Empty(fixture.State.Items);
    }

    [Fact]
    public async Task Register_DeduplicatesAgainstRegistrationsCompletedWhileChecking()
    {
        var fixture = new MonitorFixture();
        const string first = @"C:\fixture\reports";
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var resume = new ManualResetEventSlim(false);
        var stop = TestContext.Current.CancellationToken;
        fixture.DirectoryExists = path =>
        {
            if (path == first)
            {
                entered.SetResult();
                Assert.True(resume.Wait(TimeSpan.FromSeconds(5), stop), "First registration worker was not released");
            }
            return true;
        };
        var pending = fixture.Session.Register([first]);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), stop);
            Assert.Empty(await fixture.Session.Register([first.ToUpperInvariant()]));
        }
        finally { resume.Set(); }
        Assert.Empty(await pending);
        Assert.Equal(first.ToUpperInvariant(), Assert.Single(fixture.State.Items).Path);
        await fixture.Session.Close();
    }

    [Fact]
    public async Task Register_UnexpectedLookupErrorsPropagateWithoutSilentlySaving()
    {
        var fixture = new MonitorFixture();
        var failure = new InvalidOperationException("unexpected");
        fixture.DirectoryExists = _ => throw failure;
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Session.Register([@"C:\fixture\trial"])));
        Assert.Empty(fixture.State.Items);
        await fixture.Session.Close();
        Assert.Empty(fixture.Saves);
    }

    [Fact]
    public async Task RenameAndRemove_OnlyOperateOnRegisteredItemsAndDisabledHooksAreNotReused()
    {
        var fixture = new MonitorFixture();
        var file = fixture.AddFile("first.txt");
        var absent = new WatchItem { DisplayName = "unchanged" };
        fixture.Session.RenameTarget(absent, "ignored");
        Assert.Equal("unchanged", absent.DisplayName);
        fixture.Session.RenameTarget(file, "  測定結果  ");
        Assert.Equal("測定結果", file.DisplayName);
        fixture.Session.RenameTarget(file, null);
        Assert.Empty(file.DisplayName);
        fixture.Session.RemoveTargets([absent, file, file]);
        Assert.Empty(fixture.State.Items);
        Assert.Equal(file.Id, Assert.Single(fixture.Hooks.Disabled));
        await fixture.Session.Close();
        fixture.State.Items.Add(file);
        fixture.Session.RenameTarget(file, "ignored");
        fixture.Session.RemoveTargets([file]);
        Assert.Same(file, Assert.Single(fixture.State.Items));
        Assert.Empty(file.DisplayName);
    }

    [Fact]
    public async Task Settings_ExplicitFolderScopeAndNotificationOverridesCanReturnToTheirDefaults()
    {
        var fixture = new MonitorFixture();
        fixture.State.RecursiveFolders = true;
        var folder = fixture.AddFile("logs");
        folder.IsFolder = true;
        folder.Recursive = true;
        var file = fixture.AddFile("sample.txt");
        var absent = new WatchItem { IsFolder = true };
        fixture.Session.SetTargetRecursion(file, true);
        fixture.Session.SetTargetRecursion(absent, false);
        Assert.Null(file.RecursiveOverride);
        Assert.Null(absent.RecursiveOverride);
        fixture.Session.SetTargetRecursion(folder, false);
        Assert.False(folder.Recursive);
        Assert.False(folder.RecursiveOverride);
        fixture.Session.SetTargetRecursion(folder, null);
        Assert.True(folder.Recursive);
        Assert.Null(folder.RecursiveOverride);
        Assert.Equal(2, folder.Revision);
        fixture.State.RecursiveFolders = false;
        fixture.Session.SetTargetRecursion(folder, true);
        Assert.True(folder.Recursive);
        Assert.True(folder.RecursiveOverride);
        fixture.Session.SetTargetRecursion(folder, null);
        Assert.False(folder.Recursive);
        Assert.Null(folder.RecursiveOverride);

        fixture.Session.SetTargetNotificationWait(absent, true);
        Assert.Null(absent.WaitForStableOverride);
        fixture.Session.SetTargetNotificationWait(file, false);
        Assert.False(file.WaitForStableOverride);
        fixture.Session.SetTargetNotificationWait(file, null);
        Assert.Null(file.WaitForStableOverride);
        await fixture.Session.Close();
        fixture.Session.SetTargetRecursion(folder, false);
        fixture.Session.SetTargetNotificationWait(file, true);
        Assert.False(folder.Recursive);
        Assert.Null(file.WaitForStableOverride);
    }

    [Fact]
    public async Task SetExclusionsThenClearDeleted_AcknowledgeOnlyMatchingPendingItems()
    {
        var fixture = new MonitorFixture();
        var excluded = fixture.AddFile("ignored.tmp");
        var deleted = fixture.AddFile("removed.txt");
        var folder = fixture.AddFile("logs");
        folder.IsFolder = true;
        excluded.Unread = deleted.Unread = 1;
        excluded.LastChange = "更新";
        deleted.LastChange = "消失";
        foreach (var (key, kind) in new[] { (".", "更新"), ("ignored.tmp", "更新"), ("deleted.txt", "消失"), ("keep.txt", "更新") })
            folder.PendingFiles[key] = new(key, kind, DateTimeOffset.UnixEpoch);
        folder.Unread = folder.PendingFiles.Count;
        fixture.Session.SetExclusions("  *.tmp \r\n ");
        Assert.Equal("*.tmp", Assert.Single(fixture.State.ExcludedFilePatterns));
        Assert.Equal(0, excluded.Unread);
        Assert.Equal(1, deleted.Unread);
        Assert.Equal(3, folder.Unread);
        Assert.True(folder.PendingFiles.ContainsKey("."));
        fixture.Session.ClearDeleted();
        Assert.Equal(0, deleted.Unread);
        Assert.Equal(2, folder.Unread);
        Assert.True(folder.PendingFiles.ContainsKey("keep.txt"));
        Assert.True(folder.PendingFiles.ContainsKey("."));
        await fixture.Session.Close();
    }
}
