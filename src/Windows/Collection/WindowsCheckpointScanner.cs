using System.ComponentModel;

namespace ShareFileWatch;

internal sealed class WindowsCheckpointScanner(Func<string, bool> isInternalFile) : ICheckpointScanner
{
    private const int EntryLimit = 50_000;

    public CheckpointScan Scan(CheckpointInfo scope, CancellationToken stop)
    {
        var files = new Dictionary<string, Fingerprint>(StringComparer.OrdinalIgnoreCase);
        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>();
        pending.Push("");
        int remaining = EntryLimit;
        while (pending.TryPop(out var relative))
        {
            stop.ThrowIfCancellationRequested();
            var folder = Path.Combine(scope.Root, relative);
            try
            {
                var attributes = File.GetAttributes(folder);
                if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory)
                    throw new IOException("通常のフォルダを指定してください。");
                foreach (var entry in FileProbe.ListDirectory(folder, remaining, stop))
                {
                    remaining--;
                    var name = Path.Combine(relative, entry.Name);
                    if (entry.IsFolder)
                    {
                        if (scope.Recursive) pending.Push(name);
                    }
                    else if (!FileExclusions.Matches(name, scope.Exclusions) && !isInternalFile(Path.Combine(scope.Root, name)))
                        files[name] = entry.Fingerprint;
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception)
            {
                errors[relative] = error.Message;
            }
        }
        return new(files, errors);
    }
}
