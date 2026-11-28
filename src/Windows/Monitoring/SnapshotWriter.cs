namespace ShareFileWatch;

// 状態の複製はUIスレッドで、ディスク書き込みだけはワーカーで行う。
// 保存中に届いた要求を次のスナップショットにまとめ、書き込みを常に1本に保つ。
internal sealed class SnapshotWriter(Func<AppState> capture, Action<AppState> persist, Action changed)
{
    private long requested, persisted;
    private Task? worker;
    internal string? Error { get; private set; }

    internal void Enqueue()
    {
        requested++;
        StartIfPending();
    }

    internal async Task FlushAsync()
    {
        if (worker is { IsCompleted: false } active) await active;
        // 直前の書き込みが失敗していた場合も、終了する前に一度だけ再試行する。
        StartIfPending();
        if (worker is not null) await worker;
        if (Error is not null) throw new IOException(Error);
    }

    private void StartIfPending()
    {
        if (persisted != requested && (worker is null || worker.IsCompleted)) worker = WriteAsync();
    }

    private async Task WriteAsync()
    {
        while (persisted != requested)
        {
            long revision = requested;
            var snapshot = capture();
            try
            {
                await Task.Run(() => persist(snapshot));
                persisted = revision;
                Error = null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Error = ex.Message;
            }
            changed();
            // 同じ要求で連続して失敗しない。新しい要求または終了時に再試行する。
            if (revision == requested) break;
        }
    }
}
