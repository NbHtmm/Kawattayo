namespace ShareFileWatch;

internal sealed partial class MainWindow
{
    private async Task VerifyQuietScan(string root)
    {
        var dir = Directory.CreateDirectory(Path.Combine(root,"quiet")).FullName;
        var unc = @"\\localhost\" + dir[0] + "$" + dir[2..];
        var remote = Directory.Exists(unc) ? unc : dir;
        for (int i=0;i<40;i++) File.WriteAllText(Path.Combine(dir,$"{i}.txt"),"x");
        var files = Enumerable.Range(0,40).Select(i => new WatchItem { Path=Path.Combine(remote,$"{i}.txt") }).ToArray();
        var folder = new WatchItem { Path=remote,IsFolder=true };
        __state.Items.AddRange(files); __state.Items.Add(folder);
        int reads = 0;
        __testFileProbe = p => { Interlocked.Increment(ref reads); return FileProbe.Read(p); };
        try
        {
            await Scan(true);
            var feed = __feeds.Values.Single();
            var last = folder.LastChecked;
            SelfTest.Check(reads==40 && feed.Watcher != null && folder.Inventory?.Count==40,"shared subscription and baseline");
            for (int i=0;i<10;i++) await Scan(true);
            SelfTest.Check(reads==40 && folder.LastChecked==last,"ten idle ticks: zero file probes and zero inventories");
            File.AppendAllText(Path.Combine(dir,"0.txt"),"change");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (feed.Stamp(false,files[0].Path)==0) await Task.Delay(15,deadline.Token);
            await Scan(true);
            SelfTest.Check(reads==41 && files[0].Unread==1 && folder.Unread==1,"one notification probes one file plus its registered folder");
            await Scan(); SelfTest.Check(reads==81,"manual scan bypasses quiet schedule");
            await Scan(true); SelfTest.Check(reads==81,"no periodic audit");
            feed.Failed=true;
            await Scan(true);
            SelfTest.Check(reads==121 && folder.Inventory?.Count==40,"notification loss reconciles every subscriber");
            // Deterministic fallback: a missing folder cannot start a watcher. Recover it and verify a new baseline.
            var down = new WatchItem { Path=Path.Combine(dir,"offline"), IsFolder=true };
            __state.Items.Add(down);
            await Scan(true);
            var first = down.LastChecked;
            await Task.Delay(20); await Scan(true);
            SelfTest.Check(down.LastChecked > first && FeedStatus(down).Contains("毎回照会"),"failed watcher polls every tick");
            Directory.CreateDirectory(down.Path); File.WriteAllText(Path.Combine(down.Path,"offline.txt"),"x");
            await Task.Delay(5100); await Scan(true);
            SelfTest.Check(FeedStatus(down)=="通知待機中" && down.Inventory?.Count==1 && down.QueryMs.HasValue,"reconnected watcher takes inventory");

            using var cancel = new CancellationTokenSource();
            try {
                FolderProbe.Read(dir,true,_ => { cancel.Cancel(); return false; },cancel.Token);
                throw new InvalidOperationException("cancelled enumeration returned a partial result");
            } catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
            // An unrelated file is NOT pulled into a notification-only lookup.
            var previous = down.Inventory!;
            File.WriteAllText(Path.Combine(down.Path,"unreported.txt"),"x");
            var changedPath = Path.Combine(down.Path,"reported.txt"); File.WriteAllText(changedPath,"x");
            var delta = FolderProbe.ReadChanges(down.Path,false,previous,[changedPath],_=>false,default);
            SelfTest.Check(delta.Files?.Count==2 && delta.Files.ContainsKey("reported.txt") && previous.Count==1,"notification reads only the named path, preserving baseline");
            var oldPath = down.Path + "-old";
            Directory.Move(down.Path,oldPath);
            Directory.CreateDirectory(down.Path);
            string fresh = Path.Combine(down.Path,"replacement.txt"); File.WriteAllText(fresh,"new");
            using var recovery = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (FeedStatus(down)!="通知待機中" || down.Inventory?.ContainsKey("replacement.txt")!=true)
            { await Task.Delay(100,recovery.Token); await Scan(true); }
            File.AppendAllText(fresh,"!");
            while (down.Inventory!["replacement.txt"].Bytes != 4)
            { await Task.Delay(100,recovery.Token); await Scan(true); }
            SelfTest.Check(down.Inventory!.Count==1,"replacement directory scanned and watched for subsequent edits");

            string a = Path.Combine(down.Path,"A"), b = Directory.CreateDirectory(Path.Combine(a,"B")).FullName;
            var child = Path.Combine(b,"child.txt"); File.WriteAllText(child,"x");
            var enumerated = new List<string>(); int childChecks=0;
            var tree = FolderProbe.ReadChanges(down.Path,true,down.Inventory,[child,b,a],p=>{ if(p==child) childChecks++; return false; },default,enumerated.Add);
            SelfTest.Check(enumerated.SequenceEqual([a,b]) && childChecks==1,"overlapping notifications enumerate each directory once and skip covered file");
            File.Delete(child); Directory.Delete(b); Directory.Delete(a); File.WriteAllText(a,"file");
            var replaced = FolderProbe.ReadChanges(down.Path,true,tree.Files!,[a],_=>false,default);
            SelfTest.Check(replaced.Files!.ContainsKey("A") && !replaced.Files.ContainsKey(@"A\B\child.txt"),"file replaces old subtree on a single name notification");

            __testFileProbe = _ => new(ProbeState.Unavailable);
            feed.Failed=true; await Scan(true); await Task.Delay(5100); await Scan(true);
            SelfTest.Check(FeedStatus(files[0]).Contains("照会失敗（毎回照会）"),"active watcher with failed probe shows polling mode");
        }
        finally { ResetQuietScan(); __testFileProbe=null; __state.Items.Clear(); __state.History.Clear(); Save(); RefreshRows(); }
    }
}