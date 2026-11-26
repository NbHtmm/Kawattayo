namespace ShareFileWatch;

// 監視対象の登録・設定・一覧表示。ネットワークのポーリングとは分けて扱う。
/// @brief 受信箱と監視のライフサイクルを管理するメイン画面。
internal sealed partial class MainWindow
{
    private readonly CheckBox __keepInTrayOption = new() { Text = "通知領域に常駐", AutoSize = true }; //!< 利用者が切り替える監視・表示オプション。
    private readonly CheckBox __readOnOpen = new() { Text = "ファイル・フォルダを開いたら既読にする", AutoSize = true }; //!< 利用者が切り替える監視・表示オプション。
    private readonly CheckBox __darkModeOption = new() { Text = "ダークモード", AutoSize = true }; //!< 利用者が切り替える監視・表示オプション。
    private readonly CheckBox __allRecursive = new() { Text = "サブフォルダも再帰的に監視", AutoSize = true }; //!< 利用者が切り替える監視・表示オプション。
    private readonly SettingsTabs __settingsTabs = new(); //!< 監視対象と動作設定を切り替えるタブ。
    private SelectionFooter __targetSelection = null!; //!< 監視対象一覧の選択内容を表示する欄。
    private Button __addTarget = null!; //!< 登録種別を選択するボタン。

    /// @brief 設定画面の各領域と操作を構築する。
    private void BuildManagement()
    {
        var page = WatchTheme.Layout(rows: 1);
        page.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        __settingsTabs.TabPages.Add("監視対象");
        __settingsTabs.TabPages.Add("動作と表示");
        foreach (TabPage tab in __settingsTabs.TabPages)
        {
            tab.Tag = WatchTheme.Role.Window;
            tab.UseVisualStyleBackColor = false;
        }
        var body = WatchTheme.Layout(rows: 1);
        body.Padding = new Padding(18, 12, 18, 12);
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        body.Controls.Add(new SettingsGroup("登録した監視対象", BuildManagementList(), fill: true), 0, 0);
        __settingsTabs.TabPages[0].Controls.Add(body);
        var settings = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(18, 12, 18, 12) };
        settings.Controls.Add(BuildOptionsCard());
        __settingsTabs.TabPages[1].Controls.Add(settings);
        page.Controls.Add(__settingsTabs, 0, 0);

        __management = new Form
        {
            Text = $"{AppDefaults.AppName} | 設定",
            Icon = __baseIcon,
            Font = Font,
            BackColor = WatchTheme.Window,
            ForeColor = WatchTheme.Ink,
            Size = new Size(1130, 640),
            AutoScaleDimensions = new SizeF(96, 96),
            AutoScaleMode = AutoScaleMode.Dpi,
            MinimumSize = new Size(520, 360),
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.CenterParent
        };
        // 画面テストでは実際のデスクトップを覆わない位置に設定画面を置く。
        if (__testMode)
        {
            __management.StartPosition = FormStartPosition.Manual;
            __management.Location = new Point(-25000, -25000);
        }
        __management.FormClosing += (_, e) =>
        {
            // 設定画面だけを閉じた場合は非表示にして再利用し、アプリ終了時は閉じる。
            if (!__exiting && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                __management.Hide();
            }
        };
        __management.Controls.Add(page);
        foreach (var target in new Control[] { __settingsTabs.TabPages[0], body, __targetList })
        {
            EnableDrop(target);
        }
    }


    /// @brief 設定画面の各領域と操作を構築する。
    private Control BuildOptionsCard()
    {
        var card = WatchTheme.Layout(rows: 4);
        card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        card.AutoSize = true;
        card.Dock = DockStyle.Top;
        card.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        var cadence = WatchTheme.Label("監視間隔（秒）", 10, bold: true);
        cadence.Margin = new Padding(0, 7, 6, 0);
        __pollIntervalInput.AccessibleName = "監視間隔（秒）";
        __pollIntervalInput.Margin = new Padding(0, 5, 14, 0);
        __pollIntervalInput.ValueChanged += (_, _) => { __state.PollSeconds = (int)__pollIntervalInput.Value; Save(); StartTimer(); };
        __keepInTrayOption.Margin = new Padding(0, 7, 15, 0);
        __keepInTrayOption.Checked = __state.KeepInTray;
        __keepInTrayOption.CheckedChanged += (_, _) =>
        {
            // 常駐設定が変わった場合だけ保存・反映し、表示同期による二重処理を避ける。
            if (__keepInTrayOption.Checked != __state.KeepInTray)
            {
                SetKeepInTray(__keepInTrayOption.Checked);
            }
        };
        __pause.Click += (_, _) => TogglePause();
        var prior = __state.Items.Where(x => x.IsFolder).Select(x => x.Recursive).Distinct().ToArray();
        __state.RecursiveFolders ??= prior.Length == 1 ? prior[0] : prior.Length == 0 ? false : null;
        // 再帰設定の有効・無効・混在をチェックボックスの三状態で表す。
        __allRecursive.CheckState = __state.RecursiveFolders switch
        {
            true => CheckState.Checked,
            false => CheckState.Unchecked,
            null => CheckState.Indeterminate
        };
        __allRecursive.CheckStateChanged += (_, _) =>
        {
            __state.RecursiveFolders = __allRecursive.Checked;
            foreach (var item in __state.Items.Where(x => x.IsFolder))
            {
                item.SetRecursive(__allRecursive.Checked);
            }

            Save();
            RefreshRows();
            StartTimer(immediate: true);
        };
        card.Controls.Add(new SettingsGroup("監視", WatchTheme.Stack(WatchTheme.Actions(cadence, __pollIntervalInput, __pause), __allRecursive)), 0, 0);
        __description.ForeColor = WatchTheme.Muted;
        __description.Tag = WatchTheme.Role.Muted;
        __description.Margin = new Padding(0, 2, 0, 0);
        __readOnOpen.Checked = __state.ReadOnFileOpen;
        __readOnOpen.CheckedChanged += (_, _) => { __state.ReadOnFileOpen = __readOnOpen.Checked; Save(); };
        __darkModeOption.Checked = __state.DarkMode;
        __darkModeOption.CheckedChanged += (_, _) =>
        {
            __state.DarkMode = __darkModeOption.Checked;
            ApplyTheme();
            RefreshManagementRows();
            Save();
        };
        __readOnOpen.Margin = new Padding(0, 8, 0, 2);
        __renotifyUnread.Checked = __state.RenotifyUnread;
        __renotifyUnread.CheckedChanged += (_, _) => { __state.RenotifyUnread = __renotifyUnread.Checked; Save(); };
        card.Controls.Add(new SettingsGroup("通知と既読", WatchTheme.Stack(__keepInTrayOption, __description, __readOnOpen, __renotifyUnread)), 0, 1);
        card.Controls.Add(new SettingsGroup("表示", WatchTheme.Stack(__darkModeOption, BuildDisplaySizeControl())), 0, 2);
        card.Controls.Add(new SettingsGroup("除外するファイル", BuildExclusionOptions()), 0, 3);
        return card;
    }

    /// @brief 設定画面の各領域と操作を構築する。
    private Control BuildManagementList()
    {
        var card = WatchTheme.Card(rows: 3);
        card.Margin = Padding.Empty;
        card.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        card.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        card.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        __summary.Margin = new Padding(0, 0, 0, 6);
        __summary.Font = WatchTheme.Bold(11);
        var pickerMenu = new ContextMenuStrip();
        pickerMenu.Items.Add("フォルダを選択…", null, async (_, _) => await ChooseTarget(true));
        pickerMenu.Items.Add("ファイルを選択…", null, async (_, _) => await ChooseTarget(false));
        __addTarget = WatchTheme.Button("＋ 追加 ▾", () => pickerMenu.Show(__addTarget, new Point(0, __addTarget.Height)), primary: true);
        __addTarget.Disposed += (_, _) => pickerMenu.Dispose();
        __summary.Anchor = AnchorStyles.Left;
        card.Controls.Add(WatchTheme.Actions(__addTarget, __summary), 0, 0);
        BuildColumns();
        card.Controls.Add(__targetList, 0, 1);
        __targetSelection = new SelectionFooter(__targetList, row => row.Tag is Guid id ? RowText(row) + (__state.Items.FirstOrDefault(x => x.Id == id)?.Error is { } error ? "  " + error : "") : "");
        card.Controls.Add(__targetSelection, 0, 2);
        EnableDrop(card);
        return card;
    }



    /// @brief 設定画面の各領域と操作を構築する。
    private void BuildColumns()
    {
        __targetList.ShowItemToolTips = true;
        __targetList.AccessibleName = "登録した監視対象";
        WatchTheme.StyleListHeader(__targetList);
        __targetList.BackColor = WatchTheme.Surface;
        __targetList.ForeColor = WatchTheme.Ink;
        __targetList.Columns.Add("名前", 190);
        __targetList.Columns.Add("状態", 100);
        __targetList.Columns.Add("最終更新", 170);
        __targetList.Columns.Add("最終照会", 170);
        __targetList.Columns.Add("種類", 84);
        var pathColumn = __targetList.Columns.Add("完全なパス", 300);
        __targetList.ClientSizeChanged += (_, _) => FitTargetPathColumn(pathColumn);
        __targetList.HandleCreated += (_, _) => FitTargetPathColumn(pathColumn);
        __targetList.ColumnWidthChanged += (_, e) =>
        {
            // パス列以外の幅が変わったときだけ、残り幅にパス列を合わせる。
            if (e.ColumnIndex < pathColumn.Index)
            {
                FitTargetPathColumn(pathColumn);
            }
        };
        __targetList.MultiSelect = false;
        BindTargetSorting();
        __targetList.DoubleClick += async (_, _) =>
        {
            if (__targetList.SelectedItems.Count == 1 && __targetList.SelectedItems[0].Tag is Guid id &&
                __state.Items.Find(item => item.Id == id) is { } item)
            {
                await OpenRegisteredTarget(item, parent: false);
            }
        };
        __targetList.MouseClick += (_, e) =>
        {
            var row = __targetList.HitTest(e.Location).Item;
            // 登録行を右クリックした場合は、その行を選択して操作メニューを開く。
            if (e.Button == MouseButtons.Right && row?.Tag is Guid id)
            {
                row.Selected = true;
                row.Focused = true;
                ShowTargetMenu(id, e.Location);
            }
        };
        __targetList.KeyDown += (_, e) =>
        {
            // 登録対象にフォーカスがなければキーボードからの行操作を行わない。
            if (__targetList.FocusedItem?.Tag is not Guid id)
            {
                return;
            }

            if (e.KeyCode == Keys.F2 && __state.Items.Find(item => item.Id == id) is { } target)
            {
                e.SuppressKeyPress = true;
                RenameTarget(target);
                return;
            }

            // メニューキーまたは Shift+F10 では、マウス位置でなくフォーカス行を使う。
            if (e.KeyData == (Keys.Shift | Keys.F10) || e.KeyCode == Keys.Apps)
            {
                e.SuppressKeyPress = true;
                ShowTargetMenu(id, __targetList.FocusedItem.Bounds.Location);
            }
        };
    }

    /// @brief 他の列の幅を除いた残りをパス列に割り当てる。
    private void FitTargetPathColumn(ColumnHeader pathColumn)
    {
        // ネイティブの一覧がまだ作成されていなければ列幅計算を待つ。
        if (!__targetList.IsHandleCreated)
        {
            return;
        }

        int usedWidth = __targetList.Columns.Cast<ColumnHeader>().Take(pathColumn.Index).Sum(column => column.Width);
        int width = Math.Max(ScaleForDisplay(__targetList, 160), __targetList.ClientSize.Width - usedWidth);
        // 必要な幅が変わったときだけ列に反映し、再計算の連鎖を防ぐ。
        if (pathColumn.Width != width)
        {
            pathColumn.Width = width;
        }
    }

    /// @brief マウスとキーボードで共通の監視対象メニューを表示する。
    private void ShowTargetMenu(Guid targetId, Point location)
    {
        if (__state.Items.Find(target => target.Id == targetId) is not { } item)
        {
            return;
        }

        var menu = BuildTargetMenu(item);
        menu.Closed += (_, _) => BeginInvoke(new Action(menu.Dispose));
        menu.Show(__targetList, location);
    }

    /// @brief 選択ダイアログで得たパスを監視対象に登録する。
    private async Task ChooseTarget(bool folder)
    {
        // 登録中でなく、ダイアログで選択が確定した場合だけ登録を開始する。
        if (!__registering && TargetPicker.Choose(__management, folder) is { } path)
        {
            await RegisterPaths([path]);
        }
    }


    /// @brief 監視対象タブでのみドロップ登録を受け付ける。
    private void EnableDrop(Control target)
    {
        target.AllowDrop = true;
        target.DragEnter += (_, e) => e.Effect = __settingsTabs.SelectedIndex == 0 && !__registering && e.Data?.GetDataPresent(DataFormats.FileDrop) == true &&
            (e.AllowedEffect & DragDropEffects.Copy) != 0 ? DragDropEffects.Copy : DragDropEffects.None;
        target.DragDrop += async (_, e) => await RegisterDrop(e.Data);
    }

    /// @brief 監視対象タブでのみドロップ登録を受け付ける。
    private async Task RegisterDrop(IDataObject? data)
    {
        // ファイル形式のドロップは「監視対象」タブが開いているときだけ受け付ける。
        if (__settingsTabs.SelectedIndex == 0 && data?.GetData(DataFormats.FileDrop) is string[] paths)
        {
            await RegisterPaths(paths);
        }
    }

    /// @brief 登録候補を種類・重複に注意して監視リストへ取り込む。
    private async Task<bool> RegisterPaths(string[] paths, bool allowMissingFile = false)
    {
        // 先の登録処理が終了するまでは、二重に登録を開始しない。
        if (__registering)
        {
            return false;
        }

        __registering = true;
        try
        {
            var checkedPaths = await Task.Run(() => paths.Select(path => InspectPath(path, allowMissingFile)).ToArray());
            // パスの確認中に画面が閉じられたら、登録結果を反映しない。
            if (__exiting || IsDisposed)
            {
                return false;
            }

            Register(checkedPaths.Where(x => x.Error is null).Select(x => (x.Path, x.IsFolder)), __state.RecursiveFolders == true);
            var errors = checkedPaths.Where(x => x.Error is not null).ToArray();
            // 登録できなかったパスがある場合だけ、通常画面で理由をまとめて知らせる。
            if (errors.Length > 0 && !__testMode)
            {
                MessageBox.Show(this, string.Join(Environment.NewLine, errors.Take(6).Select(x => $"{x.Path}: {x.Error}")), AppDefaults.AppName);
            }

            return errors.Length == 0;
        }
        finally { __registering = false; }
    }

    /// @brief 登録候補を種類・重複に注意して監視リストへ取り込む。
    private (string Path, bool IsFolder, string? Error) InspectPath(string input, bool allowMissingFile)
    {
        try
        {
            var path = FileProbe.NormalizeFolder(input);
            if (__store.IsInternalFile(path))
            {
                return (path, false, "アプリ自身のキャッシュは監視できません。");
            }
            // 存在するディレクトリはファイルではなくフォルダ監視として登録する。
            if (Directory.Exists(path))
            {
                return (path, true, null);
            }

            // ファイルが存在するか、不在でも許可する登録なら個別ファイルとして扱う。
            if (File.Exists(path) || allowMissingFile)
            {
                return (FileProbe.Normalize(input), false, null);
            }

            return (input, false, "ファイルまたはフォルダを確認できません。");
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return (input, false, ex.Message);
        }
    }

    /// @brief 登録候補を種類・重複に注意して監視リストへ取り込む。
    private void Register(IEnumerable<(string Path, bool IsFolder)> paths, bool recursive)
    {
        foreach (var (path, isFolder) in paths)
        {
            // 大文字小文字だけが違う同一パスを二重登録しない。
            if (__state.Items.Any(x => x.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            __state.Items.Add(new WatchItem { Path = path, IsFolder = isFolder, Recursive = isFolder && recursive });
        }
        Save();
        RefreshRows();
        StartTimer(immediate: true);
    }

    /// @brief 実体を削除せず監視登録だけを解除する。
    private void Unregister(WatchItem item)
    {
        // 通常操作では登録解除の確認を求め、同意がなければ監視対象を残す。
        if (!__testMode && MessageBox.Show(__management, $"監視登録を解除しますか？\n{item.Path}\n実ファイルは削除されません。", AppDefaults.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
        {
            return;
        }

        __state.Items.Remove(item);
        Save();
        RefreshRows();
        StartTimer(immediate: true);
    }

    /// @brief 通知領域への常駐設定を切り替える。
    private void SetKeepInTray(bool enabled)
    {
        __state.KeepInTray = enabled;
        // 常駐チェックが設定値と異なる場合だけ表示を同期する。
        if (__keepInTrayOption.Checked != enabled)
        {
            __keepInTrayOption.Checked = enabled;
        }

        __tray.Visible = enabled && !__testMode;
        Save();
        RefreshRows();
    }


    /// @brief 選択行を再利用しながら監視結果を設定一覧へ反映する。
    private void RefreshManagementRows()
    {
        // 同じ行を再利用して、定期照会のたびに選択とスクロール位置が飛ぶのを防ぐ。
        var byId = __targetList.Items.Cast<ListViewItem>().Where(row => row.Tag is Guid).ToDictionary(row => (Guid)row.Tag!);
        __targetList.BeginUpdate();
        try
        {
            foreach (var item in __state.Items)
            {
                // 対応する行がまだない対象だけ新規作成し、既存行の選択を保つ。
                if (!byId.Remove(item.Id, out var row))
                {
                    row = new ListViewItem { Tag = item.Id };
                    while (row.SubItems.Count < __targetList.Columns.Count)
                    {
                        row.SubItems.Add("");
                    }

                    __targetList.Items.Add(row);
                }
                UpdateManagementRow(row, item);
            }
            foreach (var unused in byId.Values)
            {
                __targetList.Items.Remove(unused);
            }
            SortTargetRows();
        }
        finally { __targetList.EndUpdate(); }
        __targetSelection.Sync();
    }

    /// @brief 選択行を再利用しながら監視結果を設定一覧へ反映する。
    private void UpdateManagementRow(ListViewItem row, WatchItem item)
    {
        var health = item.Error is not null ? "⚠ 監視失敗" : item.LastChecked is null ? "未照会" : item.Baseline == ProbeState.Missing ? "対象なし" : "正常";
        if (!item.IsFolder && FileExclusions.Matches(item.Path, __state.ExcludedFilePatterns))
        {
            health = "除外中";
        }
        string[] values = [TargetDisplayName(item), health, Time(item.LatestFileWriteAt, true), Time(item.LastChecked, true), item.IsFolder ? "フォルダ" : "ファイル", item.Path];
        for (int i = 0; i < values.Length; i++)
        {
            // セルの表示値が変わった場合だけ一覧を書き換える。
            if (row.SubItems[i].Text != values[i])
            {
                row.SubItems[i].Text = values[i];
            }
        }

        row.ToolTipText = item.Error ?? $"{item.Path}\n{item.LastChange}";
        row.BackColor = item.Error is not null ? WatchTheme.FailureSurface : item.Unread > 0 ? WatchTheme.Unread : WatchTheme.Surface;
        row.ForeColor = item.Error is not null ? WatchTheme.FailureInk : WatchTheme.Ink;
    }

    /// @brief 設定一覧に表示する名前または日時を整形する。
    private static string DisplayName(string path)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        return string.IsNullOrEmpty(name) ? path : name;
    }

    /// @brief 設定一覧に表示する名前または日時を整形する。
    private static string Time(DateTimeOffset? time, bool includeYear = false) => time?.ToLocalTime().ToString(includeYear ? "yyyy/MM/dd HH:mm:ss" : "MM/dd HH:mm:ss") ?? "未確認";
}
