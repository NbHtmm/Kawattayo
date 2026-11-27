namespace ShareFileWatch;

// Win32 への照会と、ワーカー側で確定させる差分をまとめる。UIの状態は読み書きしない。
internal sealed class WindowsWatchProbe(Func<string, bool> isInternalFile)
{
    internal WatchObservation Read(WatchScanRequest target, CancellationToken cancellation = default, Dictionary<string,Fingerprint>? baseline = null, string[]? paths = null, string[]? priorExclusions = null)
    {
        FolderResult? folder = null;
        ProbeResult? file = null;
        if (target.IsFolder)
        {
            bool Excluded(string path) => FileExclusions.Matches(path,target.Exclusions) || isInternalFile(path);
            folder = (paths != null && baseline != null ? FolderProbe.ReadChanges(target.Path,target.Recursive,baseline,paths,Excluded,cancellation)
                : FolderProbe.Read(target.Path,target.Recursive,Excluded,cancellation)) with { Exclusions = target.Exclusions };
            // Saving the app's own cache changes its parent directory's timestamp.
            if (folder.Files != null && target.WatchDirectoryTime && !isInternalFile(Path.Combine(target.Path, AppDefaults.SettingsFileName)))
            {
                try { folder = folder with { DirectoryStamp = Directory.GetLastWriteTimeUtc(target.Path).ToFileTimeUtc() }; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { folder = new(null, ex.Message); }
            }
        }
        else if (isInternalFile(target.Path))
        {
            file = new(ProbeState.Unavailable, Error: "アプリ自身のキャッシュは監視できません");
        }
        else
        {
            file = FileProbe.Read(target.Path);
        }
        var at = DateTimeOffset.Now;
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
