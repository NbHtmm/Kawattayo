namespace ShareFileWatch;

internal sealed class SqliteCheckpointRepository(string database, Func<string, bool> isInternalFile) : ICheckpointRepository
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
        var (info, files) = db.ReadCheckpoint(id, stop);
        // 旧版で記録された内部ファイルも、今回から「削除」と誤判定しない。
        foreach (var name in files.Keys.Where(path => isInternalFile(Path.Combine(info.Root, path))).ToArray())
            files.Remove(name);
        return (info, files);
    }

    public void Save(CheckpointInfo info, Dictionary<string, Fingerprint> files, CancellationToken stop)
    {
        using var db = new InventoryDb(database, create: true);
        db.SaveCheckpoint(info, files, stop);
    }
}
