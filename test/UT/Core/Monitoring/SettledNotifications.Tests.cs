using ShareFileWatch;
using Xunit;

public sealed class SettledNotificationsTests
{
    [Fact]
    public void Observe_NewVersionAndUnavailableIntervalRestartTheWait()
    {
        var pending = new SettledNotifications();
        var id = Guid.NewGuid();
        var alerts = new List<(string Path, string Kind)>();
        var sample = new Fingerprint(100, 1, 10);
        pending.Queue(id, "folder/report.txt", "report.txt", "更新");

        Observe(100, true);
        Observe(109, true);
        Assert.Empty(alerts);

        sample = sample with { Bytes = 12 };
        Observe(110, true);
        Observe(118, false);
        Observe(150, true);
        Observe(159, true);
        Assert.Empty(alerts);

        Observe(160, true);
        Observe(200, true);
        Assert.Equal(("folder/report.txt", "更新"), Assert.Single(alerts));
        Assert.False(pending.HasPending(id));

        void Observe(long now, bool available) =>
            pending.Observe(id, available, now, 10, _ => sample, _ => true,
                (path, kind) => alerts.Add((path, kind)));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Observe_ReadAndMissingFilesAreDropped(bool unread, bool present)
    {
        var pending = new SettledNotifications();
        var id = Guid.NewGuid();
        pending.Queue(id, "report.txt", "report.txt", "更新");
        pending.Observe(id, true, 0, 0, _ => present ? new Fingerprint(100, 1, 10) : null,
            _ => unread, (_, _) => throw new InvalidOperationException("Unexpected notification."));
        Assert.Equal(0, pending.Count);
    }

    [Fact]
    public void RemoveAndRetain_KeepOnlyRegisteredPendingFiles()
    {
        var pending = new SettledNotifications();
        var main = Guid.NewGuid();
        var removed = Guid.NewGuid();
        pending.Queue(main, "a.txt", "a.txt", "更新");
        pending.Queue(main, "b.txt", "b.txt", "更新");
        pending.Queue(removed, "c.txt", "c.txt", "更新");
        pending.Remove(main, "A.TXT");
        pending.Retain(new HashSet<Guid> { main });
        Assert.Equal("b.txt", Assert.Single(pending.Paths(main)));
        Assert.Empty(pending.Paths(removed));
        Assert.Equal(1, pending.Count);
    }

    [Fact]
    public void Invalidate_DiscardsPreviousStabilitySample()
    {
        var pending = new SettledNotifications();
        var id = Guid.NewGuid();
        int count = 0;
        pending.Queue(id, "report.txt", "report.txt", "更新");
        Observe(0);
        pending.Invalidate();
        Observe(100);
        Assert.Equal(0, count);
        Observe(110);
        Assert.Equal(1, count);

        void Observe(long now) =>
            pending.Observe(id, true, now, 10, _ => new Fingerprint(100, 1, 10), _ => true, (_, _) => count++);
    }

    [Fact]
    public void Queue_ReplacesCaseVariantAndOnlyEmitsLatestKind()
    {
        var pending = new SettledNotifications();
        var id = Guid.NewGuid();
        pending.Queue(id, "Report.txt", "Report.txt", "出現");
        pending.Queue(id, "report.txt", "report.txt", "更新");

        Assert.True(pending.HasPending(id));
        Assert.Equal("report.txt", Assert.Single(pending.Paths(id)));
        var notifications = new List<(string, string)>();
        pending.Observe(id, true, 100, 0, _ => new Fingerprint(1, 1, 1),
            _ => true, (path, kind) => notifications.Add((path, kind)));
        Assert.Equal(("report.txt", "更新"), Assert.Single(notifications));
        Assert.False(pending.HasPending(id));
    }

    [Fact]
    public void Remove_LastEntryDropsOnlyItsOwnRegistration()
    {
        var pending = new SettledNotifications();
        var first = Guid.NewGuid();
        var other = Guid.NewGuid();
        pending.Queue(first, "report.txt", "report.txt", "更新");
        pending.Queue(other, "report.txt", "report.txt", "更新");
        pending.Remove(Guid.NewGuid(), "report.txt");
        pending.Remove(first, "REPORT.TXT");
        pending.Remove(first, "REPORT.TXT");

        Assert.False(pending.HasPending(first));
        Assert.True(pending.HasPending(other));
        Assert.Equal(1, pending.Count);
    }

    [Fact]
    public void Observe_FilesWithDifferentChangeTimesSettleIndependently()
    {
        var pending = new SettledNotifications();
        var id = Guid.NewGuid();
        var other = Guid.NewGuid();
        var alerts = new List<string>();
        var versions = new Dictionary<string, Fingerprint>
        {
            ["first.txt"] = new(10, 1, 5),
            ["second.txt"] = new(10, 1, 7)
        };
        pending.Queue(id, "first.txt", "first.txt", "更新");
        pending.Queue(other, "first.txt", "first.txt", "更新");
        Observe(0);
        pending.Queue(id, "second.txt", "second.txt", "更新");
        Observe(5);
        Observe(10);
        Assert.Equal("first.txt", Assert.Single(alerts));
        Assert.Equal("second.txt", Assert.Single(pending.Paths(id)));
        Observe(15);
        Assert.Equal(new[] { "first.txt", "second.txt" }, alerts);
        Assert.False(pending.HasPending(id));
        Assert.True(pending.HasPending(other));

        void Observe(long now) => pending.Observe(id, true, now, 10,
            key => versions.GetValueOrDefault(key), _ => true, (path, _) => alerts.Add(path));
    }

    [Fact]
    public void Observe_MissingRegistrationDoesNotCallAnyObservers()
    {
        var pending = new SettledNotifications();
        pending.Queue(Guid.NewGuid(), "report.txt", "report.txt", "更新");
        pending.Observe(Guid.NewGuid(), true, 10, 0,
            _ => throw new InvalidOperationException("Unexpected fingerprint."),
            _ => throw new InvalidOperationException("Unexpected unread lookup."),
            (_, _) => throw new InvalidOperationException("Unexpected alert."));
        Assert.Equal(1, pending.Count);
    }

}
