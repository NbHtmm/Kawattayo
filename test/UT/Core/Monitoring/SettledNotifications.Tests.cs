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
}
