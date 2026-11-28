using System.IO.Enumeration;

namespace ShareFileWatch;

/// @brief ファイル名だけに適用する、大文字小文字を区別しない除外ルール。
internal static class FileExclusions
{
    private static readonly char[] s_invalid = Path.GetInvalidFileNameChars()
        .Where(c => c is not '*' and not '?').ToArray();

    /// @brief 設定画面の複数行から、初めて見つけた不正なルールを報告する。
    public static string[] Parse(string text)
    {
        var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (lines.Select(x => x.Trim()).FirstOrDefault(x => x.Length != 0 && !IsPattern(x)) is { } invalid)
        {
            throw new ArgumentException($"除外ルールにはファイル名だけを指定してください（* と ? は使用できます）：{invalid}");
        }

        return Normalize(lines);
    }

    /// @brief 古い状態や手で編集された状態を安全な比較用配列に正規化する。
    public static string[] Normalize(IEnumerable<string>? patterns) =>
        (patterns ?? []).Select(x => x?.Trim()).OfType<string>().Where(IsPattern)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    public static bool Same(string[]? left, string[] right) =>
        ReferenceEquals(left, right) || ((left ?? []).Length == right.Length &&
            (right.Length == 0 || new HashSet<string>(left!, StringComparer.OrdinalIgnoreCase).SetEquals(right)));

    /// @brief ファイル本体やフォルダに触れず、名前の末尾部分だけを照合する。
    public static bool Matches(string path, string[]? patterns)
    {
        if (patterns is not { Length: > 0 })
        {
            return false;
        }

        var name = Path.GetFileName(path.AsSpan());
        foreach (var pattern in patterns)
        {
            if (FileSystemName.MatchesSimpleExpression(pattern, name, ignoreCase: true))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsPattern(string pattern) =>
        pattern.Length != 0 && pattern is not "." and not ".." && pattern.IndexOfAny(s_invalid) < 0;
}
