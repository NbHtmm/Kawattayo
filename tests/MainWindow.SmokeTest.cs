namespace ShareFileWatch;

// Windows GUIの回帰試験専用。通常の起動では実行しない。
/// @brief 受信箱と監視のライフサイクルを管理するメイン画面。
internal sealed partial class MainWindow
{
    // 同じGUIのイベント処理をテスト用キャッシュとファイルで通す。
    /// @brief テスト専用キャッシュで一連の GUI 操作を検証する。
    internal async Task ExerciseUi(string path, string cache, Action<bool> setOpen, string? previewDirectory)
    {
        VerifyEmptyLayout(previewDirectory);
        await VerifyQuietScan(Path.GetDirectoryName(path)!);
        SelfTest.Check(__allRecursive.Checked && !__readOnOpen.Checked && !__state.NotifyDeleted && __state.ExcludedFilePatterns.SequenceEqual(["~$*"]), "first-run defaults");
        __readOnOpen.Checked = true;
        __state.NotifyDeleted = true;
        await VerifyManualScan(path, cache);
        VerifyHistoryTheme(path, previewDirectory);
        await VerifyFileAcknowledgement(path, setOpen);
        var folder = Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(path)!, "gui-folder")).FullName;
        var child = Directory.CreateDirectory(Path.Combine(folder, "sub")).FullName;
        var changed = Path.Combine(child, "changed.txt");
        var stable = Path.Combine(child, "stable.txt");
        File.WriteAllText(changed, "test");
        File.WriteAllText(stable, "test");
        await VerifyFolderRegistration(path, folder);
        await VerifyFolderTree(folder, child, changed, previewDirectory);
        await VerifyNestedAcknowledgement(changed, stable, folder, setOpen);
        await VerifyOutageRecovery(folder, previewDirectory);
        await VerifyIndependentShares();
        await VerifyTargetOptions(path);
        await VerifyExclusionSettings(Path.GetDirectoryName(path)!);
        VerifyBatchOperations();
        VerifyTrayLifetime();
    }

    /// @brief 登録前の空画面と最小サイズの設定画面を確認する。
    private void VerifyEmptyLayout(string? previewDirectory)
    {
        SelfTest.Check(__inboxEmpty.Visible && !__scanNow.Enabled, "empty state disables unavailable actions");
        // プレビュー出力先が指定されたときだけ空画面の画像を保存する。
        if (previewDirectory is not null)
        {
            SavePreview(this, previewDirectory, "preview-empty.png");
        }

        ShowManagement();
        SelfTest.Check(!__inboxActionBar.Visible && !__inboxColumns.Visible, "empty inbox hides irrelevant actions and headings");
        var managementSize = __management.Size;
        __management.Size = __management.MinimumSize;
        __settingsTabs.SelectedIndex = 1;
        var options = (Panel)__settingsTabs.TabPages[1].Controls[0];
        options.ScrollControlIntoView(__darkModeOption);
        SelfTest.Check(options.AutoScroll && __darkModeOption.Visible && !__targetList.Visible && options.ClientRectangle.Contains(
            options.RectangleToClient(__darkModeOption.RectangleToScreen(__darkModeOption.ClientRectangle))), "display option is reachable in compact settings");
        __settingsTabs.SelectedIndex = 0;
        SelfTest.Check(__targetList.Visible && __targetList.Items.Count == 0 && __addTarget.Visible && !__darkModeOption.Visible && __targetList.ClientSize.Height > 50,
            "compact targets page shows registration and list together");
        __management.Size = managementSize;
        VerifyDisplayTypography();
        SelfTest.Check(!__trayMenu.Items.Cast<ToolStripItem>().Any(x => x.Text == "監視対象と設定"), "tray omits settings shortcut");
    }

    /// @brief 手動巡回の多重押下と同一ファイルの再更新を確認する。
    private async Task VerifyManualScan(string path, string cache)
    {
        await RegisterPaths([path]);
        await Scan();
        // マーカーを置き、差分ゼロの巡回がキャッシュを置き換えないことを実ファイルで確認する。
        File.SetLastWriteTimeUtc(cache, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var stamp = File.GetLastWriteTimeUtc(cache);
        int enableChanges = 0;
        EventHandler countEnableChange = (_, _) => enableChanges++;
        __scanNow.EnabledChanged += countEnableChange;
        try
        {
            __scanNow.PerformClick();
            SelfTest.Check(__isScanning && __scanNow.Enabled, "manual refresh stays enabled during scan");
            __scanNow.PerformClick(); // busy guard ignores repeated clicks.
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (__isScanning)
            {
                await Task.Delay(10, deadline.Token);
            }

            SelfTest.Check(enableChanges == 0, "refresh does not force focus onto the adjacent action");
        }
        finally { __scanNow.EnabledChanged -= countEnableChange; }
        SelfTest.Check(File.GetLastWriteTimeUtc(cache) == stamp, "idle scans do not rewrite the cache");
        await File.AppendAllTextAsync(path, "changed");
        await Scan();
        __targetList.Items[0].Selected = true;
        SelfTest.Check(__scanNow.Enabled && __inboxActionBar.Visible, "unread actions are ready");
        SelfTest.Check(__state.Items[0].Unread > 0, "GUI shows unread");
        SelfTest.Check(__targetList.Items[0].SubItems[3].Text == __state.Items[0].LatestFileWriteAt!.Value.ToLocalTime().ToString("yyyy/MM/dd HH:mm:ss") &&
            __targetList.Items[0].SubItems[4].Text == __state.Items[0].LastChecked!.Value.ToLocalTime().ToString("yyyy/MM/dd HH:mm:ss"), "settings shows year in both timestamps");
        SelfTest.Check(__inbox.Nodes.Count == 1, "main only shows unread targets");
        SelfTest.Check(__lastAlertCount == 1 && ReferenceEquals(__tray.Icon, __unreadIcon), "new unread notifies and badges icon");
        await File.AppendAllTextAsync(path, "repeat");
        await Scan();
        SelfTest.Check(__inboxSummary.Text == "未読 1件", "repeat file update counts once");
    }

    /// @brief 履歴選択とテーマがウィンドウ再表示後も維持されることを確認する。
    private void VerifyHistoryTheme(string path, string? previewDirectory)
    {
        ShowHistory();
        var lightSurface = __inbox.BackColor;
        SelfTest.Check(Text == AppDefaults.WindowTitle && __management.Text == $"{AppDefaults.AppName} | 設定" && __historyWindow!.Text == $"{AppDefaults.AppName} | 変更履歴", "titles identify the main and child windows");
        __historyRows!.Items[0].Selected = true;
        var historySelection = __historyRows.Items[0].Tag;
        VerifyHistoryColumnSorting();
        RefreshHistory();
        SelfTest.Check(__historyRows.SelectedItems.Count == 1 && Equals(__historyRows.SelectedItems[0].Tag, historySelection), "history keeps selection during refresh");
        // 画像出力が必要な試験だけ、ライト表示の履歴と設定を撮影する。
        if (previewDirectory is not null)
        {
            SavePreview(__historyWindow!, previewDirectory, "preview-history-light.png");
            __settingsTabs.SelectedIndex = 1;
            SavePreview(__management, previewDirectory, "preview-options-light.png");
            __settingsTabs.SelectedIndex = 0;
        }
        __darkModeOption.Checked = true;
        SelfTest.Check(__addTarget.Visible && __addTarget.Parent != __targetList, "add button is separate from target rows");
        var normal = Size;
        for (int i = 0; i < 12; i++)
        {
            Width = normal.Width + 40 * i;
            Update();
        }
        __historyWindow!.Width = 1060;
        SelfTest.Check(__historyRows!.Columns.Cast<ColumnHeader>().Sum(c => c.Width) >= __historyRows.ClientSize.Width, "wide history header fills window");
        // 画像出力が必要な試験だけ、ダーク表示の各画面を撮影する。
        if (previewDirectory is not null)
        {
            SavePreview(__management, previewDirectory, "preview-targets-dark.png");
            SavePreview(this, previewDirectory, "preview-inbox-wide-dark.png");
            SavePreview(__historyWindow, previewDirectory, "preview-history-wide-dark.png");
            __settingsTabs.SelectedIndex = 1;
            SavePreview(__management, previewDirectory, "preview-options-dark.png");
            __settingsTabs.SelectedIndex = 0;
        }
        Size = normal;
        SelfTest.Check(__store.Load().State.DarkMode && __inbox.BackColor == WatchTheme.Surface &&
            __targetList.BackColor == WatchTheme.Surface && __historyRows!.BackColor == WatchTheme.Surface &&
            (SystemInformation.HighContrast || lightSurface != __inbox.BackColor), "dark theme applies immediately and persists");
        __historyWindow!.Close();
        ShowHistory();
        SelfTest.Check(__historyRows!.BackColor == WatchTheme.Surface, "reopened history uses selected theme");
        __darkModeOption.Checked = false;
        SelfTest.Check(!__store.Load().State.DarkMode && __historyRows.BackColor == lightSurface && __inbox.BackColor == lightSurface,
            "light theme restored without reopening windows");
        SelfTest.Check(__lastAlertCount == 0 && __historyRows!.Items.Count == 2 && __historyRows.Items[0].SubItems[2].Text == path,
            "repeat stays silent but appears in history");
        __historyWindow!.Close();
    }

    /// @brief ファイルを開けたときだけ未読が消えることを確認する。
    private async Task VerifyFileAcknowledgement(string path, Action<bool> setOpen)
    {
        __verifyFileOpen = _ => false;
        await OpenInboxItem(__inbox.Nodes[0]);
        SelfTest.Check(__state.Items[0].Unread > 0, "failed open does not acknowledge");
        setOpen(true);
        __verifyFileOpen = p => p == path;
        await OpenInboxItem(__inbox.Nodes[0]);
        __verifyFileOpen = null;
        SelfTest.Check(__state.Items[0].Unread == 0, "successful open acknowledges");
        SelfTest.Check(!ReferenceEquals(__tray.Icon, __unreadIcon) && __store.Load().State.History.Count == 2, "read clears badge but preserves history on disk");
        SelfTest.Check(__inbox.Nodes.Count == 0 && __targetList.Items[0].SubItems[3].Text != "未確認", "read removed from main but settings retains timestamp");
    }

    /// @brief 登録ドロップと再帰設定を登録状態に照らして確認する。
    private async Task VerifyFolderRegistration(string path, string folder)
    {
        var drop = new DataObject();
        drop.SetData(DataFormats.FileDrop, new[] { folder, path });
        __settingsTabs.SelectedIndex = 1;
        await RegisterDrop(drop);
        SelfTest.Check(__state.Items.Count == 1 && !__management.AllowDrop, "options tab rejects target drops");
        __settingsTabs.SelectedIndex = 0;
        await RegisterDrop(drop);
        await Scan();
        SelfTest.Check(__targetList.Items.Count == 2 && __state.Items[1].Recursive && __targetList.Items[1].SubItems[6].Text == "デフォルト", "new folders inherit recursive default");
        SelfTest.Check(__targetList.Columns[5].Text == "種類" && __targetList.Items[0].SubItems[5].Text == "ファイル" && __targetList.Items[1].SubItems[5].Text == "フォルダ", "targets show registered file and folder kinds");
        __targetList.Items[0].Selected = false;
        __targetList.Items[1].Selected = true;
        __allRecursive.Checked = true;
        await Scan();
        SelfTest.Check(__state.Items[1].Recursive && __store.Load().State.RecursiveFolders == true, "per-folder recursion toggle");
        VerifyTargetColumnSorting();
    }

    /// @brief 入れ子の変更の選択・展開・表示とメニュー操作を確認する。
    private async Task VerifyFolderTree(string folder, string child, string changed, string? previewDirectory)
    {
        await File.AppendAllTextAsync(changed, "changed");
        await Scan();
        SelfTest.Check(__inbox.Nodes.Count == 1 && __inbox.Nodes[0].Nodes.Count == 1 &&
             __inbox.Nodes[0].Nodes[0].Nodes.Count == 1 &&
             __inbox.Nodes[0].Nodes[0].Nodes[0].Text.Contains("changed.txt"), "main tree only shows changed nested file");
        SelfTest.Check(!__inbox.Nodes[0].IsExpanded && __inbox.SelectedNode == __inbox.Nodes[0],
            "recursive folder starts collapsed without selecting hidden content");
        using var folderIcon = new Bitmap(32, 32);
        using var fileIcon = new Bitmap(32, 32);
        using var folderGraphics = Graphics.FromImage(folderIcon);
        using var fileGraphics = Graphics.FromImage(fileIcon);
        DrawInboxIcon(folderGraphics, new Rectangle(0, 0, 32, 32), folder: true);
        DrawInboxIcon(fileGraphics, new Rectangle(0, 0, 32, 32), folder: false);
        SelfTest.Check(__inbox.Nodes[0].Tag is InboxRow { IsFolder: true } &&
            __inbox.Nodes[0].Nodes[0].Nodes[0].Tag is InboxRow { IsFolder: false } &&
            folderIcon.GetPixel(4, 20).A > 0 && fileIcon.GetPixel(4, 20).A == 0,
            "folder and file use distinct drawn icons");
        SelfTest.Check(!__inboxDetails.Visible, "details do not occupy the list by default");
        __inbox.Nodes[0].Expand();
        __inbox.Nodes[0].Nodes[0].Expand();
        RefreshInbox();
        SelfTest.Check(__inbox.Nodes[0].IsExpanded && __inbox.Nodes[0].Nodes[0].IsExpanded,
            "refresh preserves user-expanded branches");
        SelfTest.Check(__inbox.Nodes[0].Nodes[0].Nodes[0].Text.Contains("取得不可"), "unknown editor is explicitly labeled");
        __inbox.SelectedNode = __inbox.Nodes[0].Nodes[0].Nodes[0];
        SelfTest.Check(__selectedPath.Text == changed && __selectedScope.Text.EndsWith("このファイル"), "detail pane shows exact path and acknowledgment scope");
        VerifyExplorerContextMenu(folder, child, changed);
        await VerifyCopyMenu(folder, changed);
        // プレビュー要求時だけ、ウィンドウをリサイズして一覧と詳細の配置を検証する。
        if (previewDirectory is not null)
        {
            SavePreview(__management, previewDirectory, "preview-settings.png");
            SavePreview(this, previewDirectory, "preview-inbox.png");
            var original = Size;
            Size = MinimumSize;
            PerformLayout();
            SelfTest.Check(GetInboxColumns(__inbox.Width, __inbox.DeviceDpi).Action > 0, "row action fits at minimum size");
            __toggleDetails.PerformClick();
            SelfTest.Check(__inboxDetails.Visible && __inboxDetails.ClientRectangle.Contains(__selectedPath.Bounds), "details fit at minimum size");
            SavePreview(this, previewDirectory, "preview-compact.png");
            __toggleDetails.PerformClick();
            Size = original;
            VerifyDensePreview(previewDirectory, folder);
        }
    }

    /// @brief フォルダを開いた際に指定範囲だけが既読になることを確認する。
    private async Task VerifyNestedAcknowledgement(string changed, string stable, string folder, Action<bool> setOpen)
    {
        await VerifyExplorerDefaultOpen(changed, folder);
        await File.AppendAllTextAsync(changed, "unread again");
        await Scan();
        SelfTest.Check(__lastAlertCount == 1, "read file notifies again after a new change");
        await File.AppendAllTextAsync(changed, "repeat nested");
        await Scan();
        SelfTest.Check(__lastAlertCount == 0, "repeat nested file stays silent");
        SelfTest.Check(__inboxSummary.Text == "未読 1件" && __inbox.Nodes[0].Tag is InboxRow { Kind: "未読 1件" } && __inbox.Nodes[0].Nodes[0].Nodes[0].Tag is InboxRow { Kind: "更新" }, "repeat nested file counts once in summary and rows");
        await File.AppendAllTextAsync(stable, "another file");
        await Scan();
        SelfTest.Check(__lastAlertCount == 1, "different nested file notifies independently");
        VerifyInboxColumnSorting();
        VerifyFolderAcknowledgementDuringUpdate(folder);
        await VerifyParentFolderMenu(folder, setOpen);
        await File.AppendAllTextAsync(changed, "unread after opening parent");
        await Scan();
        __inbox.SelectedNode = Flatten(__inbox.Nodes).Single(x => x.Tag is InboxRow { Label: "stable.txt" });
        nint ackPoint = InboxClickPoint(__inbox.LayoutRow(__inbox.SelectedNode, __inbox.SelectedNode.Bounds).Read);
        SendMessage(__inbox.Handle, 0x201, 1, ackPoint);
        SendMessage(__inbox.Handle, 0x202, 0, ackPoint);
        SelfTest.Check(__state.Items[1].PendingFiles.Count == 1, "inline read acknowledges only the clicked file");
        __readOnOpen.Checked = false;
        await OpenInboxItem(__inbox.Nodes[0]);
        await OpenInboxItem(__inbox.Nodes[0].Nodes[0]);
        SelfTest.Check(__state.Items[1].PendingFiles.Count == 1, "opening root or subfolder keeps unread when disabled");
        __readOnOpen.Checked = true;
        await OpenInboxItem(__inbox.Nodes[0].Nodes[0]);
        SelfTest.Check(__inbox.Nodes.Count == 0, "opening subfolder acknowledges its pending changes when enabled");
        await File.AppendAllTextAsync(changed, "root folder open");
        await Scan();
        await OpenInboxItem(__inbox.Nodes[0]);
        SelfTest.Check(__inbox.Nodes.Count == 0, "opening root acknowledges its pending changes when enabled");
    }

    private void VerifyFolderAcknowledgementDuringUpdate(string folder)
    {
        // 実際のルート行で、起動待ちに別のファイルが追加／更新されるタイミングを再現する。
        var row = (InboxRow)__inbox.Nodes[0].Tag!;
        var test = new WatchItem { Path = folder, IsFolder = true };
        var now = DateTimeOffset.Now;
        test.ObserveFolder(new(new() { ["old.txt"] = new(1, 1, 1), ["again.txt"] = new(1, 1, 1) }), now);
        test.ObserveFolder(new(new() { ["old.txt"] = new(1, 1, 2), ["again.txt"] = new(1, 1, 2) }), now.AddSeconds(1));
        var acknowledge = GetAcknowledgement(test, row);
        test.ObserveFolder(new(new()
        {
            ["old.txt"] = new(1, 1, 2), ["again.txt"] = new(1, 1, 3), ["new.txt"] = new(1, 1, 1)
        }), now.AddSeconds(2));
        acknowledge();
        SelfTest.Check(test.Unread == 3 && !test.PendingFiles.ContainsKey("old.txt") &&
            test.PendingFiles.ContainsKey("again.txt") && test.PendingFiles.ContainsKey("new.txt"),
            "opening a folder clears unchanged old unread but preserves new updates");
    }

    private async Task VerifyParentFolderMenu(string folder, Action<bool> setOpen)
    {
        var item = __state.Items.Single(x => x.Path == folder);
        string changed = Path.Combine("sub", "changed.txt");
        string stable = Path.Combine("sub", "stable.txt");

        async Task ClickParent()
        {
            __inbox.SelectedNode = Flatten(__inbox.Nodes).Single(x => x.Tag is InboxRow { Label: "changed.txt" });
            __verifyShellMenu = (_, menu) =>
                menu.Items.Cast<ToolStripItem>().Single(x => x.Text == "親フォルダを開く").PerformClick();
            try
            {
                SendMessage(__inbox.Handle, 0x7b, __inbox.Handle, -1);
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                while (__isOpeningTarget)
                {
                    await Task.Delay(10, deadline.Token);
                }
            }
            finally { __verifyShellMenu = null; }
        }

        setOpen(false);
        await ClickParent();
        SelfTest.Check(item.PendingFiles.ContainsKey(changed), "failed parent menu open keeps file unread");
        setOpen(true);
        __readOnOpen.Checked = false;
        await ClickParent();
        SelfTest.Check(item.PendingFiles.ContainsKey(changed), "parent menu respects read-on-open option");
        __readOnOpen.Checked = true;
        await ClickParent();
        SelfTest.Check(!item.PendingFiles.ContainsKey(changed) && item.PendingFiles.ContainsKey(stable),
            "successful parent menu open acknowledges only selected file");
    }

    private async Task VerifyIndependentShares()
    {
        var first = new WatchItem { Path = @"\\qa-host\slow-share\slow.txt" };
        var second = new WatchItem { Path = @"\\qa-host\fast-share\fast.txt", Baseline = ProbeState.Found, Fingerprint = new(1, 1, 1) };
        __state.Items.AddRange([first, second]);
        using var release = new ManualResetEventSlim(false);
        int slowCalls = 0;
        int fastCalls = 0;
        __testFileProbe = path =>
        {
            if (path == first.Path)
            {
                Interlocked.Increment(ref slowCalls);
                if (!release.Wait(TimeSpan.FromSeconds(10)))
                {
                    throw new TimeoutException();
                }
                return new(ProbeState.Found, new(1, 1, 1));
            }

            return path == second.Path
                ? new(ProbeState.Found, new(1, 1, (ulong)Interlocked.Increment(ref fastCalls) + 1))
                : FileProbe.Read(path);
        };
        var pending = Scan();
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (second.Unread == 0)
            {
                await Task.Delay(10, deadline.Token);
            }
            SelfTest.Check(Flatten(__inbox.Nodes).Any(x => x.Tag is InboxRow { Label: "fast.txt" }),
                "fast share is visible in inbox before slow share responds");
            SelfTest.Check(!pending.IsCompleted, "slow share is still in progress");
            await Scan().WaitAsync(deadline.Token);
            SelfTest.Check(slowCalls == 1 && fastCalls == 2 && second.Unread == 2,
                "next scan skips busy share but polls other share again");
        }
        finally
        {
            release.Set();
            await pending;
            __testFileProbe = null;
            __state.Items.Remove(first);
            __state.Items.Remove(second);
            Save();
            RefreshRows();
        }
    }

    /// @brief 一時切断と回復時のステータス表示を確認する。
    private async Task VerifyOutageRecovery(string folder, string? previewDirectory)
    {
        Directory.Move(folder, folder + "-offline");
        await Scan();
        SelfTest.Check(__targetList.Items[1].SubItems[2].Text == "⚠ 監視失敗" && __targetList.Items[1].BackColor == WatchTheme.FailureSurface && __state.Items[1].Unread == 0, "outage is highlighted, not deletion");
        // プレビュー要求時だけ、監視失敗の強調表示を画像に残す。
        if (previewDirectory is not null)
        {
            SavePreview(__management, previewDirectory, "preview-monitor-failure.png");
        }

        __settingsTabs.SelectedIndex = 1;
        __unavailableTargets.PerformClick();
        SelfTest.Check(__settingsTabs.SelectedIndex == 0 && __targetList.Visible, "failure link opens targets even from options");
        Directory.Move(folder + "-offline", folder);
        await Scan();
        SelfTest.Check(__targetList.Items[1].SubItems[2].Text == "正常" && __targetList.Items[1].BackColor == WatchTheme.Surface, "status and color recover after outage");
        Unregister(__state.Items[1]);
        SelfTest.Check(__targetList.Items.Count == 1 && __addTarget.Visible && Directory.Exists(folder), "row unregister keeps folder on disk and add button available");
    }

    /// @brief 常駐設定に応じて閉じる動作が切り替わることを確認する。
    private void VerifyTrayLifetime()
    {
        __management.Hide();
        Close();
        SelfTest.Check(!Visible && !IsDisposed, "close hides when tray is enabled");
        ShowWindow();
        SelfTest.Check(Visible, "tray restoration");
        ShowManagement();
        SelfTest.Check(__keepInTrayOption.Checked, "settings shows saved choice");
        __keepInTrayOption.Checked = false;
        SelfTest.Check(!__state.KeepInTray, "settings toggle is applied immediately");
        __management.Hide();
        Close();
        SelfTest.Check(IsDisposed || !Visible, "close does not hide to tray when disabled");
    }
    /// @brief テスト用の行に対して表示・操作の契約を検証する。
    private void VerifyDensePreview(string directory, string folder)
    {
        // 表示だけの合成データ。共有先やキャッシュには一切保存しない。
        var exampleNames = new[] { "障害記録.xlsx", "出荷予定.xlsx", "作業メモ.txt", "週次報告.pdf", "工程表.xlsx", "検査結果.csv" };
        var now = DateTimeOffset.Now;
        var examples = Enumerable.Range(0, 25).Select(i => new WatchItem
        {
            Path = Path.Combine(folder, $"{i + 1:D2}_{exampleNames[i % exampleNames.Length]}"),
            LastChange = i % 6 == 4 ? "追加" : i % 6 == 3 ? "消失" : "更新",
            LastChanged = now.AddMinutes(-i - 2),
            Unread = 1
        }).ToArray();
        __state.Items.AddRange(examples);
        try
        {
            __inbox.Nodes[0].Collapse();
            RefreshInbox();
            PerformLayout();
            SelfTest.Check(__inbox.VisibleCount >= 22, "compact layout fits at least 22 rows at default size");
            SavePreview(this, directory, "preview-dense.png");
        }
        finally
        {
            foreach (var example in examples)
            {
                __state.Items.Remove(example);
            }

            RefreshInbox();
            __inbox.Nodes[0].Expand();
            __inbox.Nodes[0].Nodes[0].Expand();
        }
    }

    /// @brief テスト用の行に対して表示・操作の契約を検証する。
    private async Task VerifyExplorerDefaultOpen(string changed, string folder)
    {
        var fileRow = __inbox.Nodes[0].Nodes[0].Nodes[0];
        VerifyScaledInboxRowHitTargets(fileRow.Parent!);
        var bounds = __inbox.LayoutRow(fileRow, fileRow.Bounds).Icon;
        nint point = InboxClickPoint(bounds);
        // 通常クリックとダブルクリックで、同じ座標に対して動作が変わる。
        int opens = 0;
        string? openedPath = null;
        bool canOpen = false;
        __verifyFileOpen = path => { openedPath = path; Interlocked.Increment(ref opens); return canOpen; };
        try
        {
            __inbox.SelectedNode = __inbox.Nodes[0];
            // Post both events before yielding: the native control may wait for mouse-up.
            SelfTest.Check(PostMessage(__inbox.Handle, 0x201, 1, point) && PostMessage(__inbox.Handle, 0x202, 0, point), "mouse events queued");
            await Task.Delay(50);
            SelfTest.Check(__inbox.SelectedNode == fileRow && opens == 0, "single click selects without opening");
            SendMessage(__inbox.Handle, 0x203, 1, point);
            await WaitForItemOpen();
            SelfTest.Check(opens == 1 && openedPath == changed, "double click opens exact file via default application callback");
            SelfTest.Check(__state.Items.Single(x => x.Path == folder).Unread == 1, "failed double click does not acknowledge");
            canOpen = true;
            __readOnOpen.Checked = false;
            __verifyShellMenu = (_, menu) => menu.Items[0].PerformClick();
            ShowInboxShellMenu(fileRow, Point.Empty);
            __verifyShellMenu = null;
            await WaitForItemOpen();
            SelfTest.Check(opens == 2 && __inbox.Nodes.Count == 1 && !__store.Load().State.ReadOnFileOpen, "disabled file-open acknowledgement persists");
            __readOnOpen.Checked = true;
            SendMessage(__inbox.Handle, 0x203, 1, point);
            await WaitForItemOpen();
            SelfTest.Check(opens == 3 && __inbox.Nodes.Count == 0, "successful double click acknowledges only its unread item");
        }
        finally { __verifyFileOpen = null; }
    }

    private void VerifyScaledInboxRowHitTargets(TreeNode folder)
    {
        var original = __inbox.Font;
        var baseline = __inbox.LayoutRow(folder, folder.Bounds);
        try
        {
            __inbox.Font = GetDisplayFont(MaximumDisplayPoints, FontStyle.Regular);
            ResizeInboxMetrics();
            var large = __inbox.LayoutRow(folder, folder.Bounds);
            SelfTest.Check(large.Icon.Width > baseline.Icon.Width && large.Toggle.Width > baseline.Toggle.Width,
                "file and disclosure icons track text size");
            SelfTest.Check(folder.IsExpanded, "expanded folder is ready for disclosure hit test");
            var justOutside = new Rectangle(large.Toggle.Right, large.Toggle.Top, 1, large.Toggle.Height);
            SendMessage(__inbox.Handle, 0x203, 1, InboxClickPoint(justOutside));
            SelfTest.Check(folder.IsExpanded && !__isOpeningTarget, "outside the marker neither toggles nor opens");
            nint point = InboxClickPoint(large.Toggle);
            SendMessage(__inbox.Handle, 0x201, 1, point);
            SendMessage(__inbox.Handle, 0x202, 0, InboxClickPoint(justOutside));
            SelfTest.Check(folder.IsExpanded, "dragging outside the disclosure target does not toggle");
            SendMessage(__inbox.Handle, 0x201, 1, point);
            SendMessage(__inbox.Handle, 0x202, 0, point);
            SelfTest.Check(!folder.IsExpanded, "clicking the large disclosure target collapses the folder");
            SendMessage(__inbox.Handle, 0x201, 1, point);
            SendMessage(__inbox.Handle, 0x202, 0, point);
            SelfTest.Check(folder.IsExpanded, "clicking the same target expands the folder again");
        }
        finally
        {
            __inbox.Font = original;
            ResizeInboxMetrics();
        }
    }

    private static nint InboxClickPoint(Rectangle rect) =>
        (rect.Left + rect.Width / 2) | ((rect.Top + rect.Height / 2) << 16);

    /// @brief 開く操作がタイムアウト前に終了するのを待つ。
    private async Task WaitForItemOpen()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (__isOpeningTarget)
        {
            await Task.Delay(15, deadline.Token);
        }
    }

    /// @brief テスト用の行に対して表示・操作の契約を検証する。
    private async Task VerifyCopyMenu(string folder, string file)
    {
        // ユーザーのクリップボードを変更せず、メニューが発行する Windows 形式を確認。
        try
        {
            foreach (var path in new[] { file, folder })
            {
                var copied = new TaskCompletionSource<(string?, int)>();
                __verifyFileCopy = data =>
                {
                    int effect = data.TryGetData<MemoryStream>("Preferred DropEffect", out var stream)
                        ? BitConverter.ToInt32(stream.ToArray()) : -1;
                    copied.TrySetResult((data.GetFileDropList()[0], effect));
                };
                __verifyShellMenu = (_, menu) => menu.Items[menu.Items.Count - 1].PerformClick();
                var node = Flatten(__inbox.Nodes).First(n => ResolveInboxMenuPath(n) == path);
                ShowInboxShellMenu(node, Point.Empty);
                SelfTest.Check(await copied.Task.WaitAsync(TimeSpan.FromSeconds(5)) == (path, (int)DragDropEffects.Copy), "copy menu publishes file drop list with copy effect");
            }
            var multiple = new TaskCompletionSource<string[]>();
            __verifyFileCopy = data => multiple.TrySetResult(data.GetFileDropList().Cast<string>().ToArray());
            var rows = new[] { file, folder }.Select(p => Flatten(__inbox.Nodes).First(n => ResolveInboxMenuPath(n) == p)).ToArray();
            __inbox.SelectGesture(rows[0], Keys.None);
            __inbox.SelectGesture(rows[1], Keys.Control);
            ShowInboxShellMenu(rows[1], Point.Empty);
            SelfTest.Check((await multiple.Task.WaitAsync(TimeSpan.FromSeconds(5))).ToHashSet().SetEquals([folder, file]), "copy menu publishes every selected path");
            __inbox.SelectGesture(rows[0], Keys.None);
            bool written = false;
            __verifyFileCopy = _ => written = true;
            await CopyInboxTarget(Path.Combine(folder, "nonexistent-copy-test"));
            SelfTest.Check(!written, "missing source leaves clipboard untouched");
        }
        finally { __verifyShellMenu = null; __verifyFileCopy = null; }
    }

    /// @brief テスト用の行に対して表示・操作の契約を検証する。
    private void VerifyExplorerContextMenu(string folder, string child, string changed)
    {
        string? openedFor = null;
        __verifyShellMenu = (path, menu) =>
        {
            var labels = menu.Items.Cast<ToolStripItem>().Select(i => i.Text).ToHashSet();
            SelfTest.Check(labels.Contains("開く") && labels.Contains("コピー") && labels.Contains("パスをコピー") && labels.Contains("確認済みにする") &&
                labels.Contains("親フォルダを開く") == (path == changed) && !labels.Contains("再帰監視をオン"), "context menu is scoped to target kind");
            openedFor = path;
        };
        try
        {
            foreach (var (item, expected) in new[]
            {
                (__inbox.Nodes[0], folder),
                (__inbox.Nodes[0].Nodes[0], child),
                (__inbox.Nodes[0].Nodes[0].Nodes[0], changed)
            })
            {
                __inbox.SelectedNode = item;
                SendMessage(__inbox.Handle, 0x7b, __inbox.Handle, -1); // WM_CONTEXTMENU via keyboard.
                SelfTest.Check(openedFor == expected, "context menu targets exact file or folder");
                SelfTest.Check(__state.Items.Single(x => x.Path == folder).Unread == 1, "context menu never marks unread as read");
            }
            // 画面座標を使う通常の右クリック経路でも、選択ではなくクリック位置の行を使う。
            var target = __inbox.Nodes[0].Nodes[0];
            var screen = __inbox.PointToScreen(new Point(target.Bounds.Left + 3, target.Bounds.Top + 4));
            nint packed = unchecked((int)((ushort)screen.X | ((uint)(ushort)screen.Y << 16)));
            SendMessage(__inbox.Handle, 0x7b, __inbox.Handle, packed);
            SelfTest.Check(__inbox.SelectedNode == target && openedFor == child, "mouse context menu selects clicked row");
            // ファイル名から離れた変更列でも同じファイルを選ぶ。
            var file = __inbox.Nodes[0].Nodes[0].Nodes[0];
            var columns = GetInboxColumns(__inbox.Width, __inbox.DeviceDpi);
            screen = __inbox.PointToScreen(new Point(columns.Change + 8, file.Bounds.Top + 4));
            packed = unchecked((int)((ushort)screen.X | ((uint)(ushort)screen.Y << 16)));
            SendMessage(__inbox.Handle, 0x7b, __inbox.Handle, packed);
            SelfTest.Check(__inbox.SelectedNode == file && openedFor == changed, "context menu works over change column");
        }
        finally { __verifyShellMenu = null; }
    }

    /// @brief テスト対象の Windows コントロールにメッセージを配送する。
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern nint SendMessage(nint hwnd, uint message, nint wp, nint lp);

    /// @brief テスト対象の Windows コントロールにメッセージを配送する。
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool PostMessage(nint hwnd, uint message, nint wp, nint lp);

    /// @brief テスト画面を指定した出力先へ保存する。
    private static void SavePreview(Form form, string directory, string name)
    {
        // 他のウィンドウを取り込まず、合成データを表示したテストフォームだけ描画する。
        form.PerformLayout();
        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
        bitmap.Save(Path.Combine(directory, name), System.Drawing.Imaging.ImageFormat.Png);
    }
}
