namespace ShareFileWatch;

// Win32 への照会と、ワーカー側で確定させる差分をまとめる。UIの状態は読み書きしない。
internal sealed class WindowsWatchProbe(Func<string, bool> isInternalFile, WindowsWatchProbe.Backend? backend = null)
{
    private readonly Backend source = backend ?? Native;

    // Scripts can inject observations; only this default backend touches the filesystem or wall clock.
    internal sealed record Backend(Func<string, ProbeResult> SingleFile,
        Func<string, bool, Func<string, bool>, CancellationToken, FolderResult> FullFolder,
        Func<string, bool, Dictionary<string, Fingerprint>, string[], Func<string, bool>, CancellationToken, FolderResult> FolderDelta,
        Func<string, long> DirectoryStamp, Func<DateTimeOffset> Now);

    private static readonly Backend Native = new(FileProbe.Read, FolderProbe.Read,
        static (root, recursive, baseline, paths, exclude, stop) => FolderProbe.ReadChanges(root, recursive, baseline, paths, exclude, stop),
        static path => Directory.GetLastWriteTimeUtc(path).ToFileTimeUtc(), static () => DateTimeOffset.Now);

    internal WatchObservation Read(WatchScanRequest target, CancellationToken cancellation = default, Dictionary<string,Fingerprint>? baseline = null, string[]? paths = null, string[]? priorExclusions = null)
    {
        FolderResult? folder = null;
        ProbeResult? file = null;
        if (target.IsFolder)
        {
            bool Excluded(string path) => FileExclusions.Matches(path,target.Exclusions) || isInternalFile(path);
            folder = (paths != null && baseline != null ? source.FolderDelta(target.Path,target.Recursive,baseline,paths,Excluded,cancellation)
                : source.FullFolder(target.Path,target.Recursive,Excluded,cancellation)) with { Exclusions = target.Exclusions };
            // Saving the app's own cache changes its parent directory's timestamp.
            if (folder.Files != null && target.WatchDirectoryTime && !isInternalFile(Path.Combine(target.Path, AppDefaults.SettingsFileName)))
            {
                try { folder = folder with { DirectoryStamp = source.DirectoryStamp(target.Path) }; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { folder = new(null, ex.Message); }
            }
        }
        else if (isInternalFile(target.Path))
        {
            file = new(ProbeState.Unavailable, Error: "アプリ自身のキャッシュは監視できません");
        }
        else
        {
            file = source.SingleFile(target.Path);
        }
        var at = source.Now();
        List<FileChange>? prepared = null;
        if (baseline is not null && folder?.Files is { } files)
        {
            bool scopeChanged = !FileExclusions.Same(priorExclusions, target.Exclusions);
            prepared = FolderComparison.Compare(baseline, files, at,
                scopeChanged ? priorExclusions : null, scopeChanged ? target.Exclusions : null);
        }
        return new(target.Id, target.Revision, file, folder, at, prepared);
    }
}
