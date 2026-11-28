namespace ShareFileWatch;

// 利用側の直列実行コンテキストでコールバックする。タイマーそのものに画面型を持ち込まない。
internal interface IMonitorTimer
{
    TimeSpan Interval { get; set; }
    bool IsEnabled { get; }
    event Action? Elapsed;
    void Start();
    void Stop();
}
