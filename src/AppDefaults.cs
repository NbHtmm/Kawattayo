namespace ShareFileWatch;

/// @brief UI、永続化、監視ロジックで一致させる必要がある値。
internal static class AppDefaults
{
    internal const string AppName = "かわったよ"; //!< ウィンドウと通知に使う製品名。
    // プロジェクトのバージョンを参照し、タイトルの手動更新漏れを防ぐ。
    internal static string WindowTitle => $"{AppName} v{typeof(AppDefaults).Assembly.GetName().Version!.ToString(3)}";
    internal const string CacheFileName = "cache.json"; //!< exe に同梱して持ち運ぶ設定・履歴ファイル名。
    internal const int MinimumPollSeconds = 5; //!< 許容する最短巡回間隔（秒）。
    internal const int MaximumPollSeconds = 86_400; //!< 許容する最長巡回間隔（秒）。
    internal const int DefaultPollSeconds = 60; //!< 初回起動時の巡回間隔（秒）。
    internal const int HistoryLimit = 1_000; //!< 最新から保持する履歴件数。
}
