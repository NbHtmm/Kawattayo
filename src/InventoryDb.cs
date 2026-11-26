using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace ShareFileWatch;

// Immutable snapshots share unchanged buckets. Publishing the JSON pointer AFTER COMMIT
// lets the old JSON (including its .bak) still read precisely its own inventory.
internal sealed class InventoryDb : IDisposable
{
    private nint db;
    internal InventoryDb(string file, bool create)
    {
        try
        {
            Check(sqlite3_open_v2(file, out db, create ? 6 : 1, 0));
            if (create) Exec("CREATE TABLE IF NOT EXISTS snapshots(id TEXT PRIMARY KEY, n INTEGER NOT NULL);" +
                "CREATE TABLE IF NOT EXISTS parts(id TEXT, hash TEXT, PRIMARY KEY(id,hash)) WITHOUT ROWID;" +
                "CREATE TABLE IF NOT EXISTS files(hash TEXT,path TEXT,modified INTEGER,created INTEGER,bytes INTEGER, PRIMARY KEY(hash,path)) WITHOUT ROWID;");
        }
        catch { Dispose(); throw; }
    }
    private void Check(int rc)
    {
        if (rc != 0) throw new IOException($"フォルダ比較 DB: SQLite {rc}: {(db == 0 ? string.Empty : Marshal.PtrToStringUTF8(sqlite3_errmsg(db)))}");
    }
    internal void Exec(string sql) => Check(sqlite3_exec(db, sql, 0, 0, 0));
    public void Dispose() { if (db != 0) { sqlite3_close(db); db = 0; } }

    private sealed class Statement : IDisposable
    {
        private readonly InventoryDb owner;
        private readonly nint s;
        internal Statement(InventoryDb owner, string sql) { this.owner = owner; owner.Check(sqlite3_prepare_v2(owner.db, sql, -1, out s, 0)); }
        internal void Text(int i, string v) => owner.Check(sqlite3_bind_text16(s, i, v, v.Length * 2, -1));
        internal void Number(int i, long v) => owner.Check(sqlite3_bind_int64(s, i, v));
        internal long Number(int i) => sqlite3_column_int64(s, i);
        internal string Text(int i) => Marshal.PtrToStringUni(sqlite3_column_text16(s, i)) ?? throw new IOException("比較 DB の文字列が無効です。");
        internal bool Next() { int rc = sqlite3_step(s); if (rc == 100) return true; if (rc != 101) owner.Check(rc); return false; }
        internal void Reset() => owner.Check(sqlite3_reset(s));
        internal void Run() { Next(); Reset(); }
        public void Dispose() => sqlite3_finalize(s);
    }

    internal Dictionary<string,Fingerprint> Read(string id)
    {
        using var size = new Statement(this, "SELECT n FROM snapshots WHERE id=?"); size.Text(1,id);
        if (!size.Next()) throw new IOException("設定に対応するフォルダ比較 DB の世代がありません。");
        long count = size.Number(0);
        using var rows = new Statement(this, "SELECT f.path,f.modified,f.created,f.bytes FROM parts p JOIN files f ON f.hash=p.hash WHERE p.id=?");
        rows.Text(1,id);
        var result = new Dictionary<string,Fingerprint>(StringComparer.OrdinalIgnoreCase);
        while (rows.Next())
            if (!result.TryAdd(rows.Text(0), new(rows.Number(1),rows.Number(2),unchecked((ulong)rows.Number(3)))))
                throw new IOException("フォルダ比較 DB に重複したパスがあります。");
        if (result.Count != count) throw new IOException("フォルダ比較 DB の件数が一致しません。");
        return result;
    }

    private static int Bucket(string path)
    {
        uint hash = 2166136261; foreach (char c in path) hash = unchecked((hash ^ c) * 16777619);
        return (int)(hash & 255);
    }

    internal string Write(Dictionary<string,Fingerprint> files)
    {
        string id = Guid.NewGuid().ToString("N");
        using var snap = new Statement(this, "INSERT INTO snapshots VALUES(?,?)"); snap.Text(1,id); snap.Number(2,files.Count); snap.Run();
        using var known = new Statement(this, "SELECT 1 FROM files WHERE hash=? LIMIT 1");
        using var insert = new Statement(this, "INSERT INTO files VALUES(?,?,?,?,?)");
        using var part = new Statement(this, "INSERT INTO parts VALUES(?,?)"); part.Text(1,id);
        foreach (var group in files.GroupBy(p => Bucket(p.Key)))
        {
            var entries = group.OrderBy(p => p.Key, StringComparer.Ordinal).ToArray();
            using var bytes = new MemoryStream();
            using (var w = new BinaryWriter(bytes, Encoding.UTF8, true))
                foreach (var (path,f) in entries) { w.Write(path); w.Write(f.Modified); w.Write(f.Created); w.Write(f.Bytes); }
            string hash = Convert.ToHexString(SHA256.HashData(bytes.GetBuffer().AsSpan(0,(int)bytes.Length)));
            known.Text(1,hash); bool exists = known.Next(); known.Reset();
            if (!exists)
            {
                insert.Text(1,hash);
                foreach (var (path,f) in entries)
                {
                    insert.Text(2,path); insert.Number(3,f.Modified); insert.Number(4,f.Created); insert.Number(5,unchecked((long)f.Bytes)); insert.Run();
                }
            }
            part.Text(2,hash); part.Run();
        }
        return id;
    }

    internal void Prune(IEnumerable<string> keep)
    {
        Exec("CREATE TEMP TABLE keep(id TEXT PRIMARY KEY); CREATE TEMP TABLE obsolete(hash TEXT PRIMARY KEY);");
        using var insert = new Statement(this,"INSERT OR IGNORE INTO keep VALUES(?)");
        foreach (var id in keep) { insert.Text(1,id); insert.Run(); }
        Exec("INSERT OR IGNORE INTO obsolete SELECT hash FROM parts WHERE id NOT IN (SELECT id FROM keep);" +
             "DELETE FROM parts WHERE id NOT IN (SELECT id FROM keep); DELETE FROM snapshots WHERE id NOT IN (SELECT id FROM keep);" +
             "DELETE FROM obsolete WHERE hash IN (SELECT hash FROM parts); DELETE FROM files WHERE hash IN (SELECT hash FROM obsolete);");
    }

    private const string Dll = "winsqlite3.dll";
    [DllImport(Dll)] private static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)]string file,out nint db,int flags,nint vfs);
    [DllImport(Dll)] private static extern int sqlite3_close(nint db);
    [DllImport(Dll)] private static extern nint sqlite3_errmsg(nint db);
    [DllImport(Dll)] private static extern int sqlite3_exec(nint db,[MarshalAs(UnmanagedType.LPUTF8Str)]string sql,nint cb,nint arg,nint error);
    [DllImport(Dll)] private static extern int sqlite3_prepare_v2(nint db,[MarshalAs(UnmanagedType.LPUTF8Str)]string sql,int n,out nint stmt,nint tail);
    [DllImport(Dll,CharSet=CharSet.Unicode,ExactSpelling=true)] private static extern int sqlite3_bind_text16(nint s,int i,string v,int n,nint destructor);
    [DllImport(Dll)] private static extern int sqlite3_bind_int64(nint s,int i,long v);
    [DllImport(Dll)] private static extern long sqlite3_column_int64(nint s,int i);
    [DllImport(Dll)] private static extern nint sqlite3_column_text16(nint s,int i);
    [DllImport(Dll)] private static extern int sqlite3_step(nint s);
    [DllImport(Dll)] private static extern int sqlite3_reset(nint s);
    [DllImport(Dll)] private static extern int sqlite3_finalize(nint s);
}