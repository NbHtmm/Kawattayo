namespace ShareFileWatch;

/// @brief 相対パスごとの最新変更と未確認回数。
internal sealed record FileChange(string Relative, string Kind, DateTimeOffset DetectedAt, DateTimeOffset? ModifiedAt = null, int Count = 1)
{
    /// @brief 表示可能な最終更新日時へ変換する。
    public static DateTimeOffset? GetModifiedTime(long raw, DateTimeOffset detectedAt)
    {
        // 有効な更新時刻がないときは、日時を表示しない。
        if (raw <= 0)
        {
            return null;
        }

        try
        {
            var value = DateTimeOffset.FromFileTime(raw);
            return value <= detectedAt ? value : null;
        }
        catch (ArgumentOutOfRangeException) { return null; }
    }
}

/// @brief フォルダ内の比較用一覧または取得失敗理由。
internal sealed record FolderResult(Dictionary<string, Fingerprint>? Files, string? Error = null)
{
    public long? DirectoryStamp { get; init; }
    public string[] Exclusions { get; init; } = []; //!< この一覧を取得した時点のファイル名除外ルール。
}
