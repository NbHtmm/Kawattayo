namespace ShareFileWatch;

internal sealed partial class MonitorSession
{
    private readonly SettledNotifications __settled = new();
    internal int WaitingNotifications => __settled.Count;

    private void DeliverAlerts(WatchItem item, WatchObservation observation, IReadOnlyList<MonitorAlert> alerts)
    {
        long delay = (item.WaitForStableOverride ?? __state.WaitForStable) ? __state.StableSeconds * 1000L : 0;
        Fingerprint? Sample(string relative) => item.IsFolder
            ? observation.Folder?.Files?.GetValueOrDefault(relative) : observation.File?.Fingerprint;

        foreach (var alert in alerts)
        {
            var relative = item.IsFolder ? Path.GetRelativePath(item.Path, alert.Path) : "";
            if (delay != 0 && Sample(relative) is not null)
                __settled.Queue(item.Id, alert.Path, relative, alert.Label);
            else
            {
                __settled.Remove(item.Id, relative);
                __alertsPending.Add(alert);
            }
        }

        if (!__settled.HasPending(item.Id)) return;
        var unread = new PendingPathLookup(__state.Items);
        bool available = item.IsFolder ? observation.Folder?.Files is not null : observation.File?.State != ProbeState.Unavailable;
        __settled.Observe(item.Id, available, (long)__clock.GetElapsedTime(__clockOrigin).TotalMilliseconds, delay, Sample,
            path => unread.Contains(path) && !FileExclusions.Matches(path, __state.ExcludedFilePatterns),
            (path, label) => __alertsPending.Add(new(path, label)));
    }

    private void ResetPendingAlerts()
    {
        __settled.Invalidate();
        __settled.Retain(__state.Items.Where(item =>
            item.IsFolder || !FileExclusions.Matches(item.Path, __state.ExcludedFilePatterns)).Select(item => item.Id).ToHashSet());
    }

    internal void SetTargetNotificationWait(WatchItem item, bool? value)
    {
        if (__closing || !__state.Items.Contains(item)) return;
        item.WaitForStableOverride = value;
        UpdateSettings();
    }
}
