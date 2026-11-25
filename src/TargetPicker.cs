namespace ShareFileWatch;

/// @brief ファイルまたはフォルダを選択する Windows ダイアログ。
internal static class TargetPicker
{
    /// @brief ファイルまたはフォルダを選択する Windows ダイアログ。
    public static string? Choose(IWin32Window owner, bool folder, string? initialDirectory = null)
    {
        // フォルダ登録ではファイル選択ではなく、ディレクトリ選択ダイアログを使う。
        if (folder)
        {
            using var folders = new FolderBrowserDialog
            {
                AutoUpgradeEnabled = true,
                ShowNewFolderButton = false,
                UseDescriptionForTitle = true,
                Description = "監視するフォルダを選択",
                InitialDirectory = initialDirectory ?? ""
            };
            return folders.ShowDialog(owner) == DialogResult.OK ? folders.SelectedPath : null;
        }
        using var picker = new OpenFileDialog
        {
            AutoUpgradeEnabled = true,
            RestoreDirectory = true,
            CheckFileExists = true,
            InitialDirectory = initialDirectory ?? "",
            Title = "監視するファイルを選択",
            Filter = "すべてのファイル (*.*)|*.*"
        };
        return picker.ShowDialog(owner) == DialogResult.OK ? picker.FileName : null;
    }
}
