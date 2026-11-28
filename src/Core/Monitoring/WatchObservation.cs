namespace ShareFileWatch;

internal sealed record WatchScanRequest(Guid Id, string Path, bool IsFolder, bool Recursive, int Revision, string[] Exclusions, bool WatchDirectoryTime);
internal sealed record WatchObservation(Guid Id, int Revision, ProbeResult? File, FolderResult? Folder, DateTimeOffset At, List<FileChange>? PreparedChanges);
internal sealed record MonitorAlert(string Path, string Label);
internal sealed record ObservedFileUpdate(string Path, Fingerprint Expected);
internal sealed record ObservationOutcome(bool UnreadChanged, bool NeedsSave, IReadOnlyList<MonitorAlert> Alerts, IReadOnlyList<ObservedFileUpdate> FileUpdates);
