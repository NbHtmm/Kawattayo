namespace ShareFileWatch;

internal sealed record CheckpointInfo(string Id, string Name, string Root, bool Recursive, string[] Exclusions, DateTimeOffset At);
internal enum DifferenceKind { Added, Updated, Deleted, Unconfirmed }
internal sealed record CheckpointDifference(string Relative, DifferenceKind Kind, Fingerprint? Expected, string? Error = null)
{
    internal bool CanDownload => Kind is DifferenceKind.Added or DifferenceKind.Updated;
}

internal sealed record CheckpointScan(Dictionary<string, Fingerprint> Files, Dictionary<string, string> Errors);
internal sealed record DownloadRequest(string Source, string Relative, Fingerprint? Expected = null, string? SourceRoot = null);
internal sealed record DownloadResult(string Source, string? Saved, string? Error);

internal interface ICheckpointRepository
{
    CheckpointInfo[] List();
    (CheckpointInfo Info, Dictionary<string, Fingerprint> Files) Read(string id, CancellationToken stop);
    void Save(CheckpointInfo info, Dictionary<string, Fingerprint> files, CancellationToken stop);
}

internal interface ICheckpointScanner
{
    CheckpointScan Scan(CheckpointInfo scope, CancellationToken stop);
}

internal interface ICheckpointDownloads
{
    DownloadResult[] Save(string output, IReadOnlyList<DownloadRequest> requests, CancellationToken stop);
}
