using System.Collections.Concurrent;

namespace ShareFileWatch;

internal sealed partial class MonitorSession
{
    private const int SubscriptionLimit = 32;
    private readonly Dictionary<string, ChangeFeed> __feeds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, Checkpoint> __checks = [];
    private sealed record Checkpoint(int Revision, ChangeFeed? Feed, long Stamp, bool Reliable, int Epoch);

    // Callbacks only set dirty markers. Independent registrations in one directory share a handle.
    private sealed class ChangeFeed(string directory, bool recursive, Func<string,bool> internalFile)
    {
        public readonly ConcurrentDictionary<string,long> Names = new(StringComparer.OrdinalIgnoreCase);
        private long version;
        private readonly Dictionary<string,long> journal = new(StringComparer.OrdinalIgnoreCase);
        private long discarded;
        public (long Stamp, string[]? Paths) Snapshot(long since)
        {
            lock (journal) return (version, since < discarded ? null : journal.Where(p => p.Value > since).Select(p => p.Key).ToArray());
        }
        public long Stamp(bool folder,string path) => folder ? Interlocked.Read(ref version) : Names.GetValueOrDefault(path);
        public FileSystemWatcher? Watcher;
        private FileSystemWatcher? parentWatcher;
        public volatile bool Failed;
        public int Epoch;
        private long retry;
        private bool retired;
        private void Signal(string path)
        {
            if (internalFile(path)) return;
            lock (journal)
            {
                if (journal.Count >= 4096) { discarded = version; journal.Clear(); }
                journal[path] = Interlocked.Increment(ref version);
            }
            if (Names.ContainsKey(path)) Names.AddOrUpdate(path,1,(_,n) => n+1);
        }
        public async Task Start(long now)
        {
            if (Failed && Watcher != null) { CloseWatchers(); Epoch++; }
            if (Watcher != null || now < retry || retired) return;
            Failed = false; retry = now + 5000;
            var pair = await Task.Run<(FileSystemWatcher?, FileSystemWatcher?)>(() => {
                FileSystemWatcher? w = null;
                FileSystemWatcher? parent = null;
                try
                {
                    // Arm the parent first: replacing the directory can leave its old handle alive.
                    var normalized = Path.TrimEndingDirectorySeparator(directory);
                    if (Path.GetDirectoryName(normalized) is string parentPath)
                    {
                        parent = new FileSystemWatcher(parentPath, Path.GetFileName(normalized)) { NotifyFilter = NotifyFilters.DirectoryName };
                        parent.Created += (_,_) => Failed = true;
                        parent.Deleted += (_,_) => Failed = true;
                        parent.Renamed += (_,_) => Failed = true;
                        parent.Error += (_,_) => Failed = true;
                        parent.EnableRaisingEvents = true;
                    }
                    w = new FileSystemWatcher(directory) { IncludeSubdirectories = recursive,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime };
                    w.Created += (_,e) => Signal(e.FullPath); w.Changed += (_,e) => Signal(e.FullPath);
                    w.Deleted += (_,e) => Signal(e.FullPath); w.Renamed += (_,e) => { Signal(e.OldFullPath); Signal(e.FullPath); };
                    w.Error += (_,_) => Failed = true;
                    w.EnableRaisingEvents = true; return (w,parent);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception)
                { w?.Dispose(); parent?.Dispose(); return (null,null); }
            });
            (Watcher,parentWatcher) = pair;
            if (retired) CloseWatchers(); else if (Watcher != null) Epoch++;
        }
        private void CloseWatchers() { parentWatcher?.Dispose(); parentWatcher = null; Watcher?.Dispose(); Watcher = null; }
        public void Retire() { retired = true; CloseWatchers(); }
    }

    private Dictionary<Guid,ChangeFeed> PrepareFeeds()
    {
        var bindings = new Dictionary<Guid,ChangeFeed>();
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new Dictionary<ChangeFeed,HashSet<string>>();
        int slots = 0;
        foreach (var item in __state.Items)
        {
            if (!item.IsFolder && FileExclusions.Matches(item.Path,__state.ExcludedFilePatterns)) continue;
            var dir = item.IsFolder ? item.Path : Path.GetDirectoryName(item.Path);
            if (dir == null) continue;
            bool recursive = item.IsFolder && item.Recursive;
            string key = (recursive ? "R|" : "D|") + Path.TrimEndingDirectorySeparator(dir);
            if (!keep.Contains(key))
            {
                int cost = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(dir)) == null ? 1 : 2;
                if (slots + cost > SubscriptionLimit) continue;
                slots += cost; keep.Add(key);
            }
            if (!__feeds.TryGetValue(key,out var feed)) __feeds[key] = feed = new(dir,recursive,__store.IsInternalFile);
            bindings[item.Id] = feed;
            if (!names.TryGetValue(feed,out var set)) names[feed] = set = new(StringComparer.OrdinalIgnoreCase);
            if (!item.IsFolder) { set.Add(item.Path); feed.Names.TryAdd(item.Path,0); }
        }
        foreach (var (feed,set) in names) foreach (var p in feed.Names.Keys) if (!set.Contains(p)) feed.Names.TryRemove(p,out _);
        foreach (var key in __feeds.Keys.Where(k => !keep.Contains(k)).ToArray()) { __feeds[key].Retire(); __feeds.Remove(key); }
        var ids = __state.Items.Select(i => i.Id).ToHashSet();
        foreach (var id in __checks.Keys.Where(id => !ids.Contains(id)).ToArray()) __checks.Remove(id);
        return bindings;
    }

    internal string DescribeChangeFeed(WatchItem item)
    {
        if (__state.Paused) return "一時停止";
        if (!item.IsFolder && FileExclusions.Matches(item.Path, __state.ExcludedFilePatterns)) return "除外";
        if (!__state.LowServerLoad) return "オフ（毎回照会）";
        if (!__checks.TryGetValue(item.Id, out var checkpoint)) return "未確認";
        return checkpoint.Feed switch
        {
            null => "購読枠外（毎回照会）",
            { Watcher: not null, Failed: false } when checkpoint.Reliable => "通知待機中",
            { Watcher: not null, Failed: false } => "照会失敗（毎回照会）",
            _ => "利用不可（毎回照会）"
        };
    }

    private void ResetQuietScan()
    {
        foreach (var feed in __feeds.Values) feed.Retire();
        __feeds.Clear(); __checks.Clear();
    }
}