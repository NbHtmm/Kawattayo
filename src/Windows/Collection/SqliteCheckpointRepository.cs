namespace ShareFileWatch;

internal sealed class SqliteCheckpointRepository(string database) : ICheckpointRepository
{
    public CheckpointInfo[] List()
    {
        using var db = new InventoryDb(database, create: true);
        return db.ListCheckpoints();
    }

    public (CheckpointInfo Info, Dictionary<string, Fingerprint> Files) Read(string id, CancellationToken stop)
    {
        // コネクションを閉じてから共有フォルダを走査できるよう、ここで読み切る。
        using var db = new InventoryDb(database, create: true);
        return db.ReadCheckpoint(id, stop);
    }

    public void Save(CheckpointInfo info, Dictionary<string, Fingerprint> files, CancellationToken stop)
    {
        using var db = new InventoryDb(database, create: true);
        db.SaveCheckpoint(info, files, stop);
    }
}
