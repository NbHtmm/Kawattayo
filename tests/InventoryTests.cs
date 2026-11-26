using System.Diagnostics;
using System.Text.Json;

namespace ShareFileWatch;

internal static class InventoryTests
{
    internal static void Migrate(string root)
    {
        string json = Path.Combine(root,"legacy-inventory.json"), db = Path.ChangeExtension(json,".sqlite3");
        var state = new { Version = 1, PollSeconds = 87, Items = new[] { new { Path = root, IsFolder = true,
            Inventory = new Dictionary<string,Fingerprint> { ["前回.txt"] = new(10,20,30) } } } };
        File.WriteAllText(json, JsonSerializer.Serialize(state));
        var store = new StateStore(json);
        var migrated = store.Load().State;
        SelfTest.Check(migrated.PollSeconds == 87 && migrated.Items.Single().Inventory!["前回.txt"].Bytes == 30 && File.Exists(db) &&
            !File.ReadAllText(json).Contains("\"Inventory\":"), "split migrated inventory");
        migrated.Items[0].ObserveFolder(new(new() { ["前回.txt"] = new(11,20,31) }),DateTimeOffset.Now);
        store.Save(migrated);
        SelfTest.Check(new StateStore(json).Load().State.Items[0].Inventory!["前回.txt"].Bytes == 31, "current generation");
        File.WriteAllText(json,"{");
        var restored = new StateStore(json).Load();
        SelfTest.Check(restored.Warning != null && restored.State.Items[0].Inventory!["前回.txt"].Bytes == 30, "backup generation");
        File.Move(db,db+".held");
        string before = File.ReadAllText(json);
        try { new StateStore(json).Load(); throw new InvalidOperationException("accepted missing database"); }
        catch (IOException) { SelfTest.Check(before == File.ReadAllText(json), "missing DB leaves settings untouched"); }
    }

    internal static int Scale()
    {
        using var fixture = Directory.CreateTempSubdirectory("KawattayoSQLite-").AsFixture();
        string root = Directory.CreateDirectory(Path.Combine(fixture.FullName,"watch")).FullName;
        const int count = 30000;
        for (int d=0; d<100; d++)
        {
            var dir = Directory.CreateDirectory(Path.Combine(root,d.ToString())).FullName;
            for (int i=0;i<count/100;i++) File.WriteAllText(Path.Combine(dir,$"{i}.txt"), "x");
        }
        var json = Path.Combine(fixture.FullName,"cache.json"); var db = Path.ChangeExtension(json,".sqlite3");
        var store = new StateStore(json); var item = new WatchItem { Path=root, IsFolder=true, Recursive=true };
        var state = new AppState { Items=[item] };
        void Poll(string name, bool save)
        {
            var t=Stopwatch.StartNew(); var result=FolderProbe.Read(root,true);
            SelfTest.Check(result.Files?.Count == count,"scale enumeration");
            item.ObserveFolder(result,DateTimeOffset.Now); var scan=t.ElapsedMilliseconds;
            if(save) store.Save(state);
            Console.WriteLine($"{name}: scan+compare={scan}ms save={t.ElapsedMilliseconds-scan}ms JSON={new FileInfo(json).Length}B DB={new FileInfo(db).Length}B");
        }
        Poll("initial 30000",true);
        var stamp=File.GetLastWriteTimeUtc(db); store.Save(state);
        SelfTest.Check(stamp==File.GetLastWriteTimeUtc(db),"settings-only save does not write baseline");
        Poll("unchanged",false);
        File.AppendAllText(Path.Combine(root,"99","299.txt"),"edit"); Poll("one changed",true);
        var t=Stopwatch.StartNew(); var restored=new StateStore(json).Load().State.Items.Single();
        SelfTest.Check(restored.Inventory!.Count==count && restored.Unread==1 && restored.Inventory[@"99\299.txt"].Bytes==5,"large restart");
        Console.WriteLine($"restart={t.ElapsedMilliseconds}ms; 30000 fingerprints, one unread verified"); return 0;
    }
}