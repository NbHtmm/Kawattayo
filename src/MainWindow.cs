using System.ComponentModel;
using System.Diagnostics;

namespace ShareFileWatch;

/// @brief 受信箱と監視のライフサイクルを管理するメイン画面。
internal sealed partial class MainWindow : Form
{
    private readonly StateStore __store; //!< 実行ファイル横の永続化先。
    private readonly AppState __state; //!< 画面と巡回が共有する監視状態。
    private readonly string? __startupWarning; //!< バックアップ復元時に起動後表示する警告。
    private readonly Icon __baseIcon = LoadIcon("folder-bell.ico"); //!< 未読状態に応じて切り替えるアプリのアイコン。
    private readonly Icon __unreadIcon = LoadIcon("folder-bell-unread.ico"); //!< 未読状態に応じて切り替えるアプリのアイコン。
    /// @brief 埋め込みリソースからアイコンを読み込む。
    private static Icon LoadIcon(string name)
    {
        using var stream = typeof(MainWindow).Assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException(name);
        using var icon = new Icon(stream);
        return (Icon)icon.Clone();
    }
    private readonly NotifyIcon __tray = new() { Visible = false, Text = AppDefaults.AppName }; //!< 通知領域での表示・操作に使用する UI。
    private readonly ContextMenuStrip __trayMenu = new(); //!< 通知領域での表示・操作に使用する UI。
    private readonly System.Windows.Forms.Timer __pollTimer = new(); //!< 定期巡回を起動する UI タイマー。
    private readonly ListView __targetList = new() { View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = true, Dock = DockStyle.Fill }; //!< 登録した監視対象だけを表示する一覧。
    private readonly NumericUpDown __pollIntervalInput = new() { Minimum = AppDefaults.MinimumPollSeconds, Maximum = AppDefaults.MaximumPollSeconds, Width = 80 }; //!< 巡回間隔の秒数入力欄。
    private readonly Label __summary = new() { AutoSize = true, Margin = new Padding(10, 7, 0, 0) }; //!< 登録した監視対象の件数表示。
    private readonly ToolStripStatusLabel __status = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft }; //!< 巡回状態・保存エラーの表示欄。
    private readonly Button __pause = new() { AutoSize = true, MinimumSize = new Size(95, 31) }; //!< 巡回の停止と再開を切り替える操作。
    private readonly ToolStripMenuItem __pauseMenu = new(); //!< 巡回の停止と再開を切り替える操作。
    private bool __exiting; //!< 終了後の非同期 UI 更新を抑止するフラグ。
    private bool __isScanning => __activeScanRoots.Count != 0; //!< いずれかの共有先を照会中か。
    private bool __hasUnsavedChanges; //!< 未保存の変更があり保存再試行が必要か。
    private string? __saveError; //!< ステータス欄に表示する直近の失敗理由。
    private DateTimeOffset? __lastPoll; //!< 直近の巡回が終了した時刻。
    private readonly bool __testMode; //!< 試験中の通知・自動巡回を抑止する。
    private readonly Func<string, bool> __launchFolder; //!< フォルダを開く OS 処理の差し替え口。
    private readonly Label __description = new() { AutoSize = true, Margin = new Padding(3, 5, 3, 8) }; //!< 常駐設定に合わせて切り替える説明文。
    private bool __isOpeningTarget; //!< 対象を開く操作の多重実行を防ぐ。
    private bool __registering; //!< 監視対象を登録中かどうか。

    /// @brief 受信箱と監視のライフサイクルを管理するメイン画面。
    public MainWindow(StateStore store, bool testMode = false, Func<string, bool>? launchFolder = null)
    {
        __store = store;
        __testMode = testMode;
        __launchFolder = launchFolder ?? WindowsShell.OpenFolder;
        (__state, __startupWarning) = store.Load();
        WatchTheme.DarkMode = __state.DarkMode;
        Text = AppDefaults.WindowTitle;
        Icon = __tray.Icon = __baseIcon;
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = GetDisplayFont(DefaultDisplayPoints, FontStyle.Regular);
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(520, 360);
        Size = new Size(1080, 650);
        __pollIntervalInput.Value = __state.PollSeconds;
        BuildInbox();
        BuildManagement();
        ApplyDisplayTypography();
        DpiChanged += (_, _) => BeginInvoke(ResizeInboxMetrics);
        ConfigureTray();
        ApplyTheme();
        RefreshRows();
        __pollTimer.Tick += async (_, _) => await Scan(automatic: true);
        __scanFlush.Tick += (_, _) => FlushScanUpdates();
        __progressTimer.Tick += (_, _) => RefreshProgressCells();
        Shown += (_, _) =>
        {
            // テストは巡回のタイミングを制御するため、通常の起動処理を行わない。
            if (testMode)
            {
                return;
            }

            StartTimer(immediate: true);
            // 設定の読み込み時に警告があった場合だけ、起動後に内容を伝える。
            if (__startupWarning is not null)
            {
                MessageBox.Show(this, __startupWarning, AppDefaults.AppName);
            }
        };
        Resize += (_, _) =>
        {
            // 常駐が有効なときの最小化は、タスクバーではなく通知領域に収納する。
            if (__state.KeepInTray && WindowState == FormWindowState.Minimized)
            {
                HideToTray();
            }
        };
        FormClosing += OnClosing;
    }

    /// @brief 通知領域のメニューと操作を初期化する。
    private void ConfigureTray()
    {
        __trayMenu.Items.Add("画面を開く", null, (_, _) => ShowWindow());
        __pauseMenu.Click += (_, _) => TogglePause();
        __trayMenu.Items.Add(__pauseMenu);
        __trayMenu.Items.Add("終了", null, (_, _) => ExitApp());
        __tray.ContextMenuStrip = __trayMenu;
        __tray.DoubleClick += (_, _) => ShowWindow();
        __tray.BalloonTipClicked += (_, _) => ShowWindow();
        __tray.Visible = !__testMode && __state.KeepInTray;
    }

    /// @brief 開いている各画面へ配色を反映する。
    private void ApplyTheme()
    {
        WatchTheme.DarkMode = __state.DarkMode;
        WatchTheme.Apply(this);
        WatchTheme.Apply(__management);
        // 履歴画面をすでに開いていれば、その画面にも配色を反映する。
        if (__historyWindow is not null)
        {
            WatchTheme.Apply(__historyWindow);
        }
    }

    /// @brief 開く操作が成功し設定が有効な場合だけ既読にする。
    private async Task OpenAndConfirm(WatchItem item, string path, Action? acknowledge, Func<string, bool>? openFile = null)
    {
        // 対象を開く操作が進行中なら二重起動を防ぐ。
        if (__isOpeningTarget)
        {
            return;
        }

        __isOpeningTarget = true;
        try
        {
            bool opened = await Task.Run(() => (openFile ?? __launchFolder)(path));
            // 起動結果を待つ間に終了した場合は、画面や既読状態を更新しない。
            if (IsDisposed || __exiting)
            {
                return;
            }

            // 開けなかった対象は未読のまま残す。
            if (!opened)
            {
                // 通常操作では失敗を知らせ、無人テストではダイアログを出さない。
                if (!__testMode)
                {
                    MessageBox.Show(this, openFile is null ? "フォルダが見つからないか、アクセスできません。" : "ファイルが見つからないか、アクセスできません。", AppDefaults.AppName);
                }

                return;
            }
            // 開くことに成功し、対象がまだ登録され、設定も有効な場合だけ既読にする。
            if (acknowledge is not null && __state.Items.Contains(item) && __state.ReadOnFileOpen)
            {
                acknowledge();
                Save();
                RefreshRows();
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            // 画面を操作できる通常実行時だけ、起動例外を表示する。
            if (!__exiting && !IsDisposed && !__testMode)
            {
                MessageBox.Show(this, (openFile is null ? "フォルダを開けませんでした。" : "ファイルを開けませんでした。") + "\n" + ex.Message, AppDefaults.AppName);
            }
        }
        finally { __isOpeningTarget = false; }
    }


    /// @brief 監視を一時停止・再開し、古い巡回結果を無効化する。
    private void TogglePause()
    {
        __state.Paused = !__state.Paused;
        foreach (var scan in __runningScans) scan.Cancel();
        ResetQuietScan();
        __scanGeneration++; // 一時停止を挟んだ古い照会結果は採用しない。
        Save();
        RefreshRows();
        StartTimer(immediate: !__state.Paused);
    }

    /// @brief 設定一覧・受信箱・通知アイコンを状態と同期する。
    private void RefreshRows(bool refreshInbox = true)
    {
        // 設定一覧は表示中か、その内容を検証するテスト中に更新する。
        if (__management.Visible || __testMode)
        {
            RefreshManagementRows();
        }

        int unread = __state.Items.Count(x => x.Unread > 0);
        __summary.Text = $"登録 {__state.Items.Count} 件";
        __pause.Text = __pauseMenu.Text = __state.Paused ? "監視を再開" : "一時停止";
        __description.Text = __state.KeepInTray ? "メイン画面を閉じても通知領域で監視を続けます。" :
            "メイン画面を閉じると監視を終了します。";
        var icon = unread > 0 ? __unreadIcon : __baseIcon;
        // 未読状態に応じたアイコンが変わる場合だけウィンドウに設定する。
        if (!ReferenceEquals(Icon, icon))
        {
            Icon = icon;
        }

        // 通知領域もアイコンが切り替わる場合だけ更新する。
        if (!ReferenceEquals(__tray.Icon, icon))
        {
            __tray.Icon = icon;
        }

        __tray.Text = AppDefaults.AppName;
        // 未読一覧の内容に影響する更新だけ、ツリーの再構築を要求する。
        if (refreshInbox)
        {
            RefreshInbox();
        }

        UpdateStatus();
    }

    /// @brief 監視状況と操作可能なボタンを更新する。
    private void UpdateStatus()
    {
        __status.Text = __saveError is not null ? $"キャッシュを保存できません：{__saveError}" :
            (__state.Paused ? "一時停止中" : __isScanning ? $"照会中 {__activeScanRoots.Count}系統 ｜ 最終応答 {Time(__lastPoll)}" :
                __state.Items.Count == 0 ? "監視対象を追加してください" : $"監視中 ｜ 最終巡回 {Time(__lastPoll)}");
        // 保存エラーがないときは、巡回の失敗理由をステータスに表示する。
        if (__saveError is null && !string.IsNullOrWhiteSpace(__pollError))
        {
            __status.Text = __pollError;
        }

        int unavailable = __state.Items.Count(item => item.Error is not null);
        __unavailableTargets.Text = $"監視失敗：{unavailable}件　一覧を開く ›";
        __unavailableTargets.Visible = unavailable > 0;
        __unavailableTargets.LinkColor = WatchTheme.FailureInk;
        __unavailableTargets.BackColor = WatchTheme.FailureSurface;
        // 巡回中に無効化するとフォーカスが隣の履歴へ移る。二重実行は Scan の busy 判定で防ぐ。
        __scanNow.Enabled = !__state.Paused && __state.Items.Count > 0;
        __stopQuery.Enabled = __state.Paused || __isScanning;
        __stopQuery.Text = __state.Paused ? "監視を再開" : "照会を中断";
    }

    // 設定・差分・確認操作だけを同期保存する。変化のない巡回はキャッシュを書かない。
    /// @brief 状態を保存し、失敗時に既存データを保護する。
    private void Save()
    {
        __hasUnsavedChanges = true;
        try
        {
            __store.Save(__state);
            __saveError = null;
            __hasUnsavedChanges = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { __saveError = ex.Message; }
        UpdateStatus();
    }

    /// @brief 通知領域とメイン画面の表示を切り替える。
    private void HideToTray()
    {
        __management.Hide();
        __historyWindow?.Close();
        Hide();
        ShowInTaskbar = false;
    }

    /// @brief 通知領域とメイン画面の表示を切り替える。
    private void ShowWindow()
    {
        ShowInTaskbar = true;
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    /// @brief 常駐に戻らずアプリを終了する。
    private void ExitApp()
    {
        __exiting = true;
        Close();
    }

    /// @brief 閉じる操作を常駐または終了として処理する。
    private void OnClosing(object? sender, FormClosingEventArgs e)
    {
        // 常駐中の手動クローズは終了にせず、通知領域に戻す。
        if (!__exiting && __state.KeepInTray && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }
        // 保存に失敗した変更が残っていれば終了前に再試行する。
        if (__hasUnsavedChanges)
        {
            Save();
        }

        __exiting = true;
        __pollTimer.Stop();
        __tray.Visible = false;
    }

    /// @brief アプリ共通のショートカットを処理する。
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        // 手動更新が可能な場合だけ Ctrl+R を更新操作として処理する。
        if (keyData == (Keys.Control | Keys.R) && __scanNow.Enabled)
        {
            __scanNow.PerformClick();
            return true;
        }
        // Ctrl+, は設定画面を開くショートカットとして消費する。
        if (keyData == (Keys.Control | Keys.Oemcomma))
        {
            ShowManagement();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    /// @brief 所有するリソースを解放する。
    protected override void Dispose(bool disposing)
    {
        // 明示的な破棄ではタイマーと通知領域などの所有リソースを解放する。
        if (disposing)
        {
            __pollTimer.Dispose();
            ResetQuietScan();
            __scanFlush.Dispose();
            foreach (var scan in __runningScans) scan.Cancel();
            __progressTimer.Dispose();
            __ageTimer.Dispose();
            __management?.Dispose();
            __tray.Dispose();
            __trayMenu.Dispose();
        }
        base.Dispose(disposing);
        // 基底ウィンドウが使い終わってから、所有するアイコンを解放する。
        if (disposing)
        {
            __unreadIcon.Dispose();
            __baseIcon.Dispose();
            DisposeDisplayFonts();
        }
    }
}
