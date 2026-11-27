

namespace ShareFileWatch;

internal sealed partial class InventoryDb
{
    private void EnsureCheckpoints() => Exec("""
        CREATE TABLE IF NOT EXISTS checkpoints(id TEXT PRIMARY KEY,name TEXT NOT NULL,root TEXT NOT NULL,recursive INTEGER NOT NULL,
            exclusions TEXT NOT NULL,at INTEGER NOT NULL);
        CREATE TABLE IF NOT EXISTS checkpoint_files(id TEXT,path TEXT,modified INTEGER,created INTEGER,bytes INTEGER,
            PRIMARY KEY(id,path)) WITHOUT ROWID;
        """);

    internal CheckpointInfo[] ListCheckpoints()
    {
        EnsureCheckpoints();
        using var query = new Statement(this, "SELECT id,name,root,recursive,exclusions,at FROM checkpoints ORDER BY at DESC,id DESC");
        var result = new List<CheckpointInfo>();
        while (query.Next()) result.Add(ReadInfo(query));
        return result.ToArray();
    }

    internal (CheckpointInfo Info, Dictionary<string, Fingerprint> Files) ReadCheckpoint(string id, CancellationToken stop)
    {
        EnsureCheckpoints();
        using var header = new Statement(this, "SELECT id,name,root,recursive,exclusions,at FROM checkpoints WHERE id=?");
        header.Text(1, id);
        if (!header.Next()) throw new IOException("チェックポイントが見つかりません。");
        var info = ReadInfo(header);
        using var rows = new Statement(this, "SELECT path,modified,created,bytes FROM checkpoint_files WHERE id=?");
        rows.Text(1, id);
        var files = new Dictionary<string, Fingerprint>(StringComparer.OrdinalIgnoreCase);
        while (rows.Next())
        {
            stop.ThrowIfCancellationRequested();
            files.Add(rows.Text(0), new(rows.Number(1), rows.Number(2), unchecked((ulong)rows.Number(3))));
        }
        return (info, files);
    }

    internal void SaveCheckpoint(CheckpointInfo info, Dictionary<string, Fingerprint> files, CancellationToken stop)
    {
        EnsureCheckpoints();
        Exec("BEGIN IMMEDIATE");
        try
        {
            using var insert = new Statement(this, "INSERT INTO checkpoints VALUES(?,?,?,?,?,?)");
            insert.Text(1, info.Id); insert.Text(2, info.Name); insert.Text(3, info.Root);
            insert.Number(4, info.Recursive ? 1 : 0); insert.Text(5, string.Join('\n', info.Exclusions));
            insert.Number(6, info.At.ToUnixTimeMilliseconds()); insert.Run();
            using var row = new Statement(this, "INSERT INTO checkpoint_files VALUES(?,?,?,?,?)");
            row.Text(1, info.Id);
            foreach (var (path, f) in files)
            {
                stop.ThrowIfCancellationRequested();
                row.Text(2, path); row.Number(3, f.Modified); row.Number(4, f.Created);
                row.Number(5, unchecked((long)f.Bytes)); row.Run();
            }
            stop.ThrowIfCancellationRequested();
            Exec("COMMIT");
        }
        catch { Exec("ROLLBACK"); throw; }
    }

    private static CheckpointInfo ReadInfo(Statement row) => new(row.Text(0), row.Text(1), row.Text(2),
        row.Number(3) != 0, FileExclusions.Parse(row.Text(4)), DateTimeOffset.FromUnixTimeMilliseconds(row.Number(5)));
}
