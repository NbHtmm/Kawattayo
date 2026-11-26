namespace ShareFileWatch;

/// @brief 受信箱と監視のライフサイクルを管理するメイン画面。
internal sealed partial class MainWindow
{
    private Action<DataObject>? __verifyFileCopy = null; //!< 実際の OS 操作に代えて回帰試験が観測するコールバック。

    /// @brief 選択したファイルまたはフォルダを Explorer 貼り付け形式にする。
    private async Task CopyInboxTarget(params string[] selected)
    {
        var paths = selected.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (paths.Length == 0) return;
        var missing = await Task.Run(() => paths.FirstOrDefault(p => !File.Exists(p) && !Directory.Exists(p)));
        // 存在確認の間に画面が閉じられたらコピー操作を続けない。
        if (IsDisposed || __exiting)
        {
            return;
        }

        // 元の対象が見つからなければクリップボードを書き換えない。
        if (missing != null)
        {
            // 対話実行のときだけコピーできない理由を利用者に知らせる。
            if (!__testMode)
            {
                MessageBox.Show(this, "次の対象を確認できないためコピーしませんでした。\n" + missing, AppDefaults.AppName);
            }

            return;
        }
        // CF_HDROP と COPY (移動ではない) を渡す。実体の転送は貼り付け先の Explorer が行う。
        using var effect = new MemoryStream(BitConverter.GetBytes((int)DragDropEffects.Copy));
        var data = new DataObject();
        var files = new System.Collections.Specialized.StringCollection();
        files.AddRange(paths);
        data.SetFileDropList(files);
        data.SetData("Preferred DropEffect", false, effect);
        // テストではデータ形式だけを検証し、利用者のクリップボードには触れない。
        if (__verifyFileCopy is { } verify)
        {
            verify(data);
            return;
        }
        try
        {
            Clipboard.SetDataObject(data, true, 5, 50);
        }
        catch (System.Runtime.InteropServices.ExternalException) { MessageBox.Show(this, "クリップボードにコピーできませんでした。もう一度お試しください。", AppDefaults.AppName); }
    }

    // ラベルやセルを編集可能にせず、選んだ行を部分選択できるテキストとしても表示する。
    /// @brief 選択行の内容を部分コピーできる読み取り専用の欄。
    private sealed class SelectionFooter : TableLayoutPanel
    {
        private readonly TextBox __text = new() { ReadOnly = true, Dock = DockStyle.Fill, AccessibleName = "選択内容（コピー用）" }; //!< 選択内容を表示する読み取り専用欄。
        private readonly Func<string> __current; //!< 現在の選択からコピー可能な文字列を生成する関数。
        /// @brief 選択行の内容を部分コピーできる読み取り専用の欄。
        public SelectionFooter(ListView list, Func<ListViewItem, string> describe)
        {
            __current = () => list.SelectedItems.Count == 0 ? "" : describe(list.SelectedItems[0]);
            ColumnCount = 2;
            RowCount = 1;
            Dock = DockStyle.Bottom;
            AutoSize = true;
            Margin = Padding.Empty;
            ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            Controls.Add(new Label { Text = "選択内容", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
            Controls.Add(__text, 1, 0);
            list.SelectedIndexChanged += (_, _) => Sync();
            list.KeyDown += (_, e) =>
            {
                // 一覧にフォーカスがあるときの Ctrl+C は選択行の文字列をコピーする。
                if (e.KeyData == (Keys.Control | Keys.C))
                {
                    e.SuppressKeyPress = true;
                    CopyText(string.Join(Environment.NewLine, list.SelectedItems.Cast<ListViewItem>().Select(describe)));
                }
            };
            Sync();
        }
        /// @brief 選択内容とフッターの表示状態を同期する。
        public void Sync()
        {
            string value = __current();
            // 表示内容に変化がなければ、利用者が選択した文字範囲を維持する。
            if (__text.Text != value)
            {
                __text.Text = value;
            }

            Visible = value.Length > 0;
        }
    }
    /// @brief 選択行の各列をタブ区切りで連結する。
    private static string RowText(ListViewItem row) => string.Join("\t", row.SubItems.Cast<ListViewItem.ListViewSubItem>().Select(x => x.Text));
}
