using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

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

/// @brief Windows の検索 API を隠蔽し、共有先を読み取り専用で照会する。
internal static class FileProbe
{
    private const uint DirectoryAttribute = 0x10; //!< Win32 のディレクトリ属性ビット。
    private const uint ReparsePointAttribute = 0x400; //!< リンクをたどらないための再解析ポイント属性。
    private const int FileNotFound = 2; //!< 検索結果が存在しないことを表す Win32 コード。
    private const int NoMoreFiles = 18; //!< 検索結果が存在しないことを表す Win32 コード。
    private static readonly IntPtr s_s_invalidSearchHandle = new(-1); //!< 検索開始失敗時に返るハンドル。

    /// @brief 登録可能な絶対パスを検証して正規化する。
    public static string Normalize(string input)
    {
        string path = input.Trim().Trim('"').Replace('/', '\\');
        // 通常の絶対パスだけを受け付け、デバイス指定や検索パターンは拒否する。
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\?\") || path.StartsWith(@"\\.\") ||
            path.IndexOfAny(['*', '?', '"', '<', '>', '|', '\0']) >= 0)
        {
            throw new ArgumentException("ファイル名まで含むUNCパス、またはドライブ付きの絶対パスを入力してください。");
        }

        string root = Path.GetPathRoot(path) ?? "";
        string relative = path[root.Length..].TrimStart('\\');
        // 個別のファイル名として曖昧な構成要素は登録しない。
        if (relative.Split('\\').Any(x => x.Length == 0 || x.Contains(':') || x.EndsWith('.') || x.EndsWith(' ')))
        {
            throw new ArgumentException("監視対象は個別のファイルを指定してください。");
        }

        return Path.GetFullPath(path);
    }

    /// @brief 入力から観測値または保存状態を読み込む。
    public static ProbeResult Read(string path)
    {
        // 検索に失敗したときは、消失とアクセス不能を切り分ける。
        if (!TryFind(path, out var data, out int error))
        {
            return InterpretMissing(path, error);
        }

        // ディレクトリと再解析ポイントは通常ファイルとして比較しない。
        if ((data.Attributes & (DirectoryAttribute | ReparsePointAttribute)) != 0)
        {
            return new(ProbeState.Unavailable, Error: "通常のファイルではありません");
        }

        ulong size = ((ulong)data.SizeHigh << 32) | data.SizeLow;
        return new(ProbeState.Found, new(ToLong(data.Written), ToLong(data.Created), size));
    }

    /// @brief 親ディレクトリを照会できる場合だけ消失と判定する。
    private static ProbeResult InterpretMissing(string path, int error)
    {
        // 「ファイルなし」以外のエラーを消失通知にしない。
        if (error != FileNotFound)
        {
            return new(ProbeState.Unavailable, Error: $"参照できません (Win32 {error})");
        }

        // 親フォルダを確認できない場合は、ファイル消失と断定しない。
        var parent = Path.GetDirectoryName(path);
        // 親を列挙でき、対象だけが見つからない場合に限って消失とみなす。
        if (parent is not null && CanSeeDirectory(parent))
        {
            return new(ProbeState.Missing);
        }

        return new(ProbeState.Unavailable, Error: "親フォルダを確認できません");
    }

    /// @brief 空のディレクトリも含め親を列挙可能か確認する。
    private static bool CanSeeDirectory(string parent)
    {
        // 検索が開始できれば、親ディレクトリにはアクセスできている。
        if (TryFind(Path.Combine(parent, "*"), out _, out int error))
        {
            return true;
        }
        // 空ディレクトリの列挙は、共有によって「該当なし」を返すことがある。
        return (error == FileNotFound || error == NoMoreFiles) && Directory.Exists(parent);
    }

    /// @brief ファイル本体を開かず検索 API で属性を取得する。
    private static bool TryFind(string path, out FindData data, out int error)
    {
        var handle = FindFirstFileW(ExtendedPath(path), out data);
        // 検索を開始できなかった場合は、原因判定のため Win32 エラーを呼び出し元に返す。
        if (handle == s_s_invalidSearchHandle)
        {
            error = Marshal.GetLastWin32Error();
            return false;
        }
        // ファイル本体は開かない。一覧の検索ハンドルもここですぐ閉じる。
        FindClose(handle);
        error = 0;
        return true;
    }

    /// @brief 登録可能な絶対パスを検証して正規化する。
    public static string NormalizeFolder(string input)
    {
        var path = input.Trim().Trim('"').Replace('/', '\\');
        // フォルダも通常の絶対パスに限定し、デバイス名やワイルドカードを拒否する。
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\?\") || path.StartsWith(@"\\.\") ||
            path.IndexOfAny(['*', '?', '"', '<', '>', '|', '\0']) >= 0)
        {
            throw new ArgumentException("フォルダの絶対パスを指定してください。");
        }

        // UNC のサーバー名だけでは登録できないので、共有名まであるか確認する。
        if (path.StartsWith(@"\\") && path[2..].Split('\\', StringSplitOptions.RemoveEmptyEntries).Length < 2)
        {
            throw new ArgumentException("サーバー名と共有名を指定してください。");
        }

        var full = Path.GetFullPath(path);
        var relative = full[(Path.GetPathRoot(full)?.Length ?? 0)..];
        // ドライブ指定以外のコロンを含むパスは監視フォルダとして扱わない。
        if (relative.Contains(':'))
        {
            throw new ArgumentException("無効なフォルダ名です。");
        }

        return Path.TrimEndingDirectorySeparator(full);
    }

    /// @brief 列挙した名前とファイル属性の組。
    internal sealed record DirectoryEntry(string Name, bool IsFolder, Fingerprint Fingerprint);

    // 一覧照会のハンドルは一つのフォルダを読み終えた時点で閉じる。
    /// @brief 直下の項目を列挙し、検索ハンドルを解放する。
    internal static List<DirectoryEntry> ListDirectory(string path, int maxEntries)
    {
        var items = new List<DirectoryEntry>();
        var handle = FindFirstFileW(ExtendedPath(Path.Combine(path, "*")), out var data);
        // 列挙を開始できなかった場合は、空のディレクトリか照会エラーかを判別する。
        if (handle == s_s_invalidSearchHandle)
        {
            int error = Marshal.GetLastWin32Error();
            // ディレクトリ自体が存在して該当項目がない場合は、空一覧を正常な結果として返す。
            if ((error == FileNotFound || error == NoMoreFiles) && Directory.Exists(path))
            {
                return items;
            }

            throw new System.ComponentModel.Win32Exception(error);
        }
        try
        {
            do
            {
                // 親や自分自身への参照とリンクを除外し、監視範囲の外へ進まない。
                if (data.Name is "." or ".." || (data.Attributes & ReparsePointAttribute) != 0)
                {
                    continue;
                }

                // 列挙上限を超えたら途中までの一覧で差分を判定しない。
                if (items.Count >= maxEntries)
                {
                    throw new IOException("監視範囲の上限を超えました。");
                }

                items.Add(new(data.Name, (data.Attributes & DirectoryAttribute) != 0, new(
                    ToLong(data.Written), ToLong(data.Created), ((ulong)data.SizeHigh << 32) | data.SizeLow)));
            }
            while (FindNextFileW(handle, out data));

            int error = Marshal.GetLastWin32Error();
            // 正常な列挙終了以外のエラーは、取得に成功したことにしない。
            if (error != NoMoreFiles)
            {
                throw new System.ComponentModel.Win32Exception(error); // ERROR_NO_MORE_FILES
            }
        }
        finally { FindClose(handle); }
        return items;
    }

    /// @brief 開いている Win32 検索の次の項目を取得する。
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindNextFileW(IntPtr handle, out FindData data);

    /// @brief 長いローカルパスと UNC パスを Win32 形式へ変換する。
    private static string ExtendedPath(string path) => path.Length < 248 ? path :
        path.StartsWith(@"\\") ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;

    /// @brief FILETIME の上位・下位ワードを結合する。
    private static long ToLong(FILETIME t) => ((long)t.dwHighDateTime << 32) | (uint)t.dwLowDateTime;

    /// @brief Win32 の検索結果と同じ順序・幅の構造体。
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FindData
    {
        public uint Attributes; //!< ファイル種別を示す Win32 属性。
        public FILETIME Created, Accessed, Written; //!< 作成・アクセス・書き込みの各 FILETIME。
        public uint SizeHigh, SizeLow, Reserved0, Reserved1; //!< バイト長の上下ワードと Win32 予約領域。
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Name; //!< Win32 が返すファイル名バッファ。
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string ShortName; //!< Win32 が返すファイル名バッファ。
    }

    /// @brief 検索を開始し最初の項目とハンドルを返す。
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr FindFirstFileW(string name, out FindData data);

    /// @brief 検索ハンドルを閉じる。
    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindClose(IntPtr handle);
}
