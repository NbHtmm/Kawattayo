namespace ShareFileWatch;

/// @brief 受信箱と監視のライフサイクルを管理するメイン画面。
internal sealed partial class MainWindow
{
    private Form? __historyWindow; //!< 変更履歴を表示するウィンドウと一覧。
    private ListView? __historyRows; //!< 変更履歴を表示するウィンドウと一覧。

    /// @brief 既存の履歴画面を前面に出すか新規作成する。
    private void ShowHistory()
    {
        // すでに開いている履歴画面を前面に出し、二重に作成しない。
        if (__historyWindow is not null)
        {
            __historyWindow.Activate();
            return;
        }
        __historyRows = new ListView { View = View.Details, FullRowSelect = true, Dock = DockStyle.Fill, AccessibleName = "変更履歴", ShowItemToolTips = true };
        __historyRows.Columns.Add("検知日時", 155);
        WatchTheme.StyleListHeader(__historyRows);
        __historyRows.Columns.Add("変更", 70);
        __historyRows.Columns.Add("ファイルパス", 570);
        __historyRows.ClientSizeChanged += (_, _) => FitHistoryColumns();
        __historyRows.ColumnWidthChanged += (_, e) =>
        {
            // 日時・変更列の幅を変えたときだけ、残りのパス列幅を再計算する。
            if (e.ColumnIndex < 2)
            {
                FitHistoryColumns();
            }
        };
        __historyWindow = new Form { Text = $"{AppDefaults.AppName} | 変更履歴", Font = Font, Size = new Size(870, 440), StartPosition = FormStartPosition.CenterParent, ShowInTaskbar = false };
        __historyWindow.Controls.Add(__historyRows);
        __historyWindow.Controls.Add(new SelectionFooter(__historyRows, row => row.Tag is HistoryEntry ? RowText(row) : ""));
        __historyWindow.Icon = __baseIcon;
        WatchTheme.Apply(__historyWindow);
        __historyWindow.FormClosed += (_, _) => { __historyWindow = null; __historyRows = null; };
        RefreshHistory();
        __historyWindow.Show(this);
        FitHistoryColumns();
    }

    /// @brief 履歴のパス列を残り幅に合わせる。
    private void FitHistoryColumns()
    {
        // 履歴一覧の作成が完了する前は列幅を変更しない。
        if (__historyRows is not { IsHandleCreated: true } list || list.Columns.Count != 3)
        {
            return;
        }

        int width = Math.Max(90, list.ClientSize.Width - list.Columns[0].Width - list.Columns[1].Width);
        // 幅が変わるときだけ設定し、不要な再レイアウトを避ける。
        if (list.Columns[2].Width != width)
        {
            list.Columns[2].Width = width;
        }
    }

    /// @brief 選択を維持して履歴を新しい順に再表示する。
    private void RefreshHistory()
    {
        // 履歴画面が未作成または閉じていれば、表示の更新は不要。
        if (__historyRows is not { } list)
        {
            return;
        }

        var selected = list.SelectedItems.Cast<ListViewItem>().Select(row => row.Tag).OfType<HistoryEntry>().ToHashSet();
        list.BeginUpdate();
        try
        {
            list.Items.Clear();
            foreach (var entry in __state.History.Reverse())
            {
                list.Items.Add(new ListViewItem(new[] { entry.At.ToLocalTime().ToString("yyyy/MM/dd HH:mm:ss"), entry.Kind, entry.Path }) { Tag = entry, ToolTipText = entry.Path }).Selected = selected.Contains(entry);
            }

            // 記録がない場合は、空白ではなく履歴がない旨を表示する。
            if (list.Items.Count == 0)
            {
                list.Items.Add("履歴はありません");
            }
        }
        finally { list.EndUpdate(); }
        FitHistoryColumns();
    }

}
