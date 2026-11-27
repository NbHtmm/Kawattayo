using System.Diagnostics;

namespace ShareFileWatch;

/// @brief Windows シェルへの引き渡しを画面と既読判定から分離する。
internal static class WindowsShell
{
    /// @brief 存在するフォルダをエクスプローラーで開く。
    internal static bool OpenFolder(string folderPath) => Directory.Exists(folderPath) && Open(folderPath);

    /// @brief 存在するファイルを Windows の関連付けで開く。
    internal static bool OpenFile(string filePath) => File.Exists(filePath) && Open(filePath);

    /// @brief 引数をシェルコマンドとして組み立てず、対象を直接渡す。
    private static bool Open(string path)
    {
        // 既存プロセスに委譲した場合は Process が null でも成功。
        using var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, Verb = "open" });
        return true;
    }
}
