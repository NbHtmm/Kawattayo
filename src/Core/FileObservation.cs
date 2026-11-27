namespace ShareFileWatch;

/// @brief 照会結果。対象の消失と通信失敗は区別する。
internal enum ProbeState
{
    Found, Missing, Unavailable
}
/// @brief ファイル本体を読まずに前回と比較する時刻・サイズの組。
internal sealed record Fingerprint(long Modified, long Created, ulong Bytes);
/// @brief 個別ファイルの観測値と失敗理由。
internal sealed record ProbeResult(ProbeState State, Fingerprint? Fingerprint = null, string? Error = null);
