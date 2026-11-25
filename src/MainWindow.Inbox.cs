namespace ShareFileWatch;

/// @brief 受信箱と監視のライフサイクルを管理するメイン画面。
internal sealed partial class MainWindow
{
    private Form __management = null!; //!< 監視対象とオプションを表示する設定画面。
    private readonly InboxTree __inbox = new() { Dock = DockStyle.Fill, HideSelection = false }; //!< 未読の監視対象と配下の変更を表示するツリー。
    private readonly Label __inboxSummary = new() { AutoSize = true }; //!< 未読ファイル数の表示。
    private readonly Label __emptyInboxDetail = new() { AutoSize = true }; //!< 未読がない場合の案内表示。
    private readonly Label __selectedTitle = new() { AutoSize = true }; //!< 選択行の名前・時刻・既読範囲・パスを示す詳細欄。
    private readonly Label __selectedMeta = new() { AutoSize = true }; //!< 選択行の名前・時刻・既読範囲・パスを示す詳細欄。
    private readonly Label __selectedScope = new() { AutoSize = true }; //!< 選択行の名前・時刻・既読範囲・パスを示す詳細欄。
    private readonly TextBox __selectedPath = new() { ReadOnly = true, Multiline = true, Height = 56, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Top, BorderStyle = BorderStyle.FixedSingle }; //!< 選択行の名前・時刻・既読範囲・パスを示す詳細欄。
    private Panel __inboxEmpty = null!; //!< 未読がない場合の案内表示。
    private Control __inboxColumns = null!; //!< 受信箱の見出し・詳細・操作用の表示領域。
    private TableLayoutPanel __inboxDetails = null!; //!< 受信箱の見出し・詳細・操作用の表示領域。
    private Control __inboxActionBar = null!; //!< 受信箱の見出し・詳細・操作用の表示領域。
    private readonly ToolStripStatusLabel __unavailableTargets = new() { IsLink = true, Visible = false }; //!< 失敗した監視対象へ移動するリンク。
    private Button __toggleDetails = null!; //!< 詳細表示の切り替えと操作対象の説明。
    private readonly Label __actionScope = new() { AutoSize = true, Anchor = AnchorStyles.Left }; //!< 詳細表示の切り替えと操作対象の説明。
    private readonly ImageList __inboxIcons = new() { ColorDepth = ColorDepth.Depth32Bit }; //!< 種類別のツリー用アイコン。
    private Button __scanNow = null!; //!< 手動で監視を更新するボタン。
    private readonly System.Windows.Forms.Timer __ageTimer = new() { Interval = 5000 }; //!< 相対時刻の表示だけを更新するタイマー。

    /// @brief 通知ツリーの表示行と監視対象を結びつける情報。
    private sealed record InboxRow(Guid Id, string Label, string? Relative, FileChange? Change, bool IsBranch,
        DateTimeOffset? DetectedAt, DateTimeOffset? ModifiedAt, string Kind, bool IsFolder, bool Recursive = false);

    /// @brief 受信箱の対応する表示領域とイベントを構築する。
    private void BuildInbox()
    {
        BackColor = WatchTheme.Window;
        ForeColor = WatchTheme.Ink;
        var page = WatchTheme.Layout(rows: 3);
        page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        page.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        page.Controls.Add(BuildInboxHeader(), 0, 0);
        page.Controls.Add(BuildInboxContent(), 0, 1);
        var strip = new StatusStrip { Dock = DockStyle.Fill, SizingGrip = false, BackColor = WatchTheme.Surface };
        strip.Items.Add(__status);
        strip.Items.Add(__unavailableTargets);
        __unavailableTargets.Click += (_, _) => { __settingsTabs.SelectedIndex = 0; ShowManagement(); };
        page.Controls.Add(strip, 0, 2);
        Controls.Add(page);
        __inbox.AccessibleName = "未読のファイル変更";
        __inbox.ShellMenuRequested += ShowInboxShellMenu;
        __inbox.KeyDown += async (_, e) =>
        {
            // Ctrl+C は選択行の対象を特定できる場合だけファイルコピーとして処理する。
            if (e.KeyData == (Keys.Control | Keys.C) && __inbox.SelectedNode is { } n && ResolveInboxMenuPath(n) is { } path)
            {
                e.SuppressKeyPress = true;
                await CopyInboxTarget(path);
            }
        };
        __inbox.AfterSelect += (_, _) => UpdateSelectedDetails();
        __inbox.DefaultOpenRequested += async node => await OpenInboxItem(node);
        __inbox.ReadRequested += node => { __inbox.SelectedNode = node; MarkInboxRead(); };
        __ageTimer.Tick += (_, _) =>
        {
            // 非表示の受信箱では、経過時間の再描画を省く。
            if (Visible)
            {
                UpdateInboxAges();
            }
        };
        __ageTimer.Start();
    }

    /// @brief 受信箱の対応する表示領域とイベントを構築する。
    private Control BuildInboxHeader()
    {
        __scanNow = WatchTheme.Button("", async () => await Scan(), name: "scanNow");
        __scanNow.AutoSize = false;
        __scanNow.MinimumSize = Size.Empty;
        __scanNow.Size = new Size(35, 35);
        __scanNow.Padding = Padding.Empty;
        __scanNow.Paint += (_, e) => WatchTheme.DrawRefreshGlyph(__scanNow, e.Graphics);
        __scanNow.AccessibleName = "更新（Ctrl+R）";
        var hint = new ToolTip();
        hint.SetToolTip(__scanNow, __scanNow.AccessibleName);
        __scanNow.Disposed += (_, _) => hint.Dispose();
        var actions = WatchTheme.Actions(__scanNow,
            WatchTheme.Button("履歴", ShowHistory, name: "openHistory"),
            WatchTheme.Button("設定…", ShowManagement, name: "openManagement"));
        var header = WatchTheme.Layout(1, 2);
        header.Dock = DockStyle.Top;
        header.AutoSize = true;
        header.BackColor = WatchTheme.Surface;
        header.Tag = WatchTheme.Role.Surface;
        header.Padding = new Padding(12, 5, 6, 1);
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        __inboxSummary.BackColor = WatchTheme.Unread;
        __inboxSummary.Tag = WatchTheme.Role.Badge;
        __inboxSummary.ForeColor = WatchTheme.Ink;
        __inboxSummary.Font = WatchTheme.Bold(10);
        __inboxSummary.Padding = new Padding(6, 2, 6, 2);
        __inboxSummary.Margin = new Padding(0, 3, 0, 0);
        __inboxSummary.Anchor = AnchorStyles.Left;
        actions.Anchor = AnchorStyles.Right;
        header.Controls.Add(__inboxSummary, 0, 0);
        header.Controls.Add(actions, 1, 0);
        return header;
    }

    /// @brief 受信箱の対応する表示領域とイベントを構築する。
    private Control BuildInboxContent()
    {
        var content = WatchTheme.Layout(3);
        content.Padding = new Padding(10, 2, 10, 2);
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.Controls.Add(BuildUnreadList(), 0, 0);
        __inboxActionBar = BuildInboxActions();
        content.Controls.Add(__inboxActionBar, 0, 1);
        content.Controls.Add(BuildSelectedPanel(), 0, 2);
        return content;
    }

    /// @brief 受信箱の対応する表示領域とイベントを構築する。
    private Control BuildInboxActions()
    {
        __toggleDetails = WatchTheme.Button("詳細 ▾", () =>
        {
            __inboxDetails.Visible = !__inboxDetails.Visible;
            __toggleDetails.Text = __inboxDetails.Visible ? "詳細 ▴" : "詳細 ▾";
        }, name: "toggleDetails");
        __actionScope.ForeColor = WatchTheme.Muted;
        __actionScope.Tag = WatchTheme.Role.Muted;
        __actionScope.Margin = new Padding(4, 0, 0, 4);
        var bar = WatchTheme.Layout(1, 2);
        bar.AutoSize = true;
        bar.Margin = new Padding(0, 6, 0, 0);
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bar.Controls.Add(__actionScope, 0, 0);
        bar.Controls.Add(__toggleDetails, 1, 0);
        return bar;
    }

    /// @brief 受信箱の対応する表示領域とイベントを構築する。
    private Control BuildUnreadList()
    {
        var list = WatchTheme.Layout(2);
        list.BackColor = WatchTheme.Surface;
        list.Tag = WatchTheme.Role.Surface;
        list.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        list.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        __inboxColumns = new InboxColumnHeader(__inbox);
        __inbox.ClientSizeChanged += (_, _) => __inboxColumns.Invalidate();
        list.Controls.Add(__inboxColumns, 0, 0);
        __inbox.BorderStyle = BorderStyle.None;
        __inbox.BackColor = WatchTheme.Surface;
        __inbox.ForeColor = WatchTheme.Ink;
        __inbox.ItemHeight = 21;
        __inbox.Indent = 18;
        __inbox.Margin = Padding.Empty;
        __inbox.ShowLines = false;
        __inbox.FullRowSelect = true;
        __inbox.DrawMode = TreeViewDrawMode.OwnerDrawText;
        __inbox.DrawNode += DrawInboxRow;
        __inboxIcons.Images.Add("file", DrawInboxIcon(folder: false));
        __inboxIcons.Images.Add("folder", DrawInboxIcon(folder: true));
        __inbox.ImageList = __inboxIcons;

        var content = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
        __inboxEmpty = new Panel { Dock = DockStyle.Fill, BackColor = WatchTheme.Surface };
        var emptyTitle = WatchTheme.Label("未読の変更はありません", 13, bold: true);
        __emptyInboxDetail.ForeColor = WatchTheme.Muted;
        __emptyInboxDetail.Tag = WatchTheme.Role.Muted;
        __emptyInboxDetail.Margin = new Padding(0, 4, 0, 0);
        var emptyText = WatchTheme.Stack(emptyTitle, __emptyInboxDetail);
        var center = WatchTheme.Layout(1);
        center.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        center.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        emptyText.Anchor = AnchorStyles.None;
        center.Controls.Add(emptyText, 0, 0);
        __inboxEmpty.Controls.Add(center);
        content.Controls.Add(__inbox);
        content.Controls.Add(__inboxEmpty);
        list.Controls.Add(content, 0, 1);
        return list;
    }

    /// @brief 受信箱の対応する表示領域とイベントを構築する。
    private Control BuildSelectedPanel()
    {
        __inboxDetails = WatchTheme.Card(rows: 4);
        __inboxDetails.Dock = DockStyle.Top;
        __inboxDetails.AutoSize = true;
        __inboxDetails.Visible = false;
        __inboxDetails.Padding = new Padding(8, 4, 8, 4);
        __inboxDetails.Margin = Padding.Empty;
        for (int i = 0; i < 4; i++)
        {
            __inboxDetails.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }

        __selectedTitle.Font = WatchTheme.Bold(10);
        __selectedMeta.ForeColor = WatchTheme.Muted;
        __selectedScope.ForeColor = WatchTheme.Muted;
        __selectedScope.Tag = __selectedMeta.Tag = WatchTheme.Role.Muted;
        __selectedPath.AccessibleName = "選択項目のパス";
        __selectedPath.Height = 43;
        __selectedPath.BackColor = WatchTheme.Window;
        __selectedPath.Tag = WatchTheme.Role.Window;
        __selectedPath.Margin = new Padding(0, 2, 0, 2);
        __inboxDetails.SizeChanged += (_, _) =>
        {
            int width = Math.Max(100, __inboxDetails.ClientSize.Width - __inboxDetails.Padding.Horizontal - 8);
            foreach (var label in new[] { __selectedTitle, __selectedMeta, __selectedScope })
            {
                label.MaximumSize = new Size(width, 0);
            }
        };
        __inboxDetails.Controls.Add(__selectedTitle, 0, 0);
        __inboxDetails.Controls.Add(__selectedMeta, 0, 1);
        __inboxDetails.Controls.Add(__selectedPath, 0, 2);
        __inboxDetails.Controls.Add(__selectedScope, 0, 3);
        return __inboxDetails;
    }

    /// @brief 監視先を再照会せず経過時間だけを更新する。
    private void UpdateInboxAges()
    {
        var now = DateTimeOffset.Now;
        foreach (var node in Flatten(__inbox.Nodes))
        {
            // 検知時刻または更新時刻がある行だけ経過時間を書き直す。
            if (node.Tag is InboxRow row && (row.DetectedAt is not null || row.ModifiedAt is not null))
            {
                PaintAge(node, row, now);
            }
        }
    }

    /// @brief 監視対象と設定を現在の状態で表示する。
    private void ShowManagement()
    {
        // すでに表示中の設定画面は、新しく開かず前面に出す。
        if (__management.Visible)
        {
            __management.Activate();
            return;
        }
        RefreshManagementRows();
        __management.Show(this);
    }

    /// @brief 選択と展開状態を維持して未読ツリーを再構築する。
    private void RefreshInbox()
    {
        // 未読が変わったときだけ組み直す。展開状態・選択は安定したキーで復元する。
        var previous = Flatten(__inbox.Nodes).ToDictionary(x => x.Name, x => x.IsExpanded, StringComparer.OrdinalIgnoreCase);
        var selected = __inbox.SelectedNode?.Name;
        var unread = __state.Items.Where(x => x.Unread > 0).OrderByDescending(x => x.LastChanged).ToArray();
        __inbox.BeginUpdate();
        try
        {
            __inbox.Nodes.Clear();
            var now = DateTimeOffset.Now;
            foreach (var item in unread)
            {
                AddInboxTarget(item, now);
            }

            TreeNode? restoreSelection = null;
            foreach (var node in Flatten(__inbox.Nodes))
            {
                // 子がある行は以前の展開状態を優先し、新規の行だけ既定の展開規則を使う。
                if (node.Nodes.Count > 0 && (previous.TryGetValue(node.Name, out var expanded)
                        ? expanded
                        : node.Tag is InboxRow { IsBranch: false, Recursive: false }))
                {
                    node.Expand();
                }

                // 再構築前と同じ識別名の行を、選択復元の候補として保持する。
                if (string.Equals(node.Name, selected, StringComparison.OrdinalIgnoreCase))
                {
                    restoreSelection = node;
                }
            }

            // 折りたたまれた配下の行を選び直すと Windows が親を開くので、見えている項目だけ選ぶ。
            while (restoreSelection?.Parent is { IsExpanded: false } collapsed)
            {
                restoreSelection = collapsed;
            }

            for (var ancestor = restoreSelection?.Parent; ancestor is not null; ancestor = ancestor.Parent)
            {
                // 親が折りたたまれている場合は、見えない子ではなく親に選択を戻す。
                if (!ancestor.IsExpanded)
                {
                    restoreSelection = ancestor;
                }
            }

            __inbox.SelectedNode = restoreSelection ?? __inbox.Nodes.Cast<TreeNode>().FirstOrDefault();
        }
        finally { __inbox.EndUpdate(); }
        __inboxSummary.Text = $"未読 {PendingPaths().Count + unread.Where(x => x.IsFolder).Sum(x => (long)x.SavedUnreadWithoutDetails)}件";

        __inbox.Visible = unread.Length > 0;
        __inboxEmpty.Visible = unread.Length == 0;
        __inboxColumns.Visible = __inboxActionBar.Visible = unread.Length > 0;
        // 未読がなくなったら選択詳細を閉じ、空の案内表示に戻す。
        if (unread.Length == 0)
        {
            __inboxDetails.Visible = false;
            __toggleDetails.Text = "詳細 ▾";
        }
        __emptyInboxDetail.Text = __state.Items.Count == 0 ? "「設定…」→「監視対象」→「＋ 追加」から登録してください。" :
            "検知した変更がここに表示されます。";
        UpdateSelectedDetails();
    }

    /// @brief 未読の対象や変更された子ファイルをツリーに追加する。
    private void AddInboxTarget(WatchItem item, DateTimeOffset now)
    {
        var path = Path.GetFileName(Path.TrimEndingDirectorySeparator(item.Path));
        // ルートなど末尾の名前が空になる対象は、パス自体を表示名にする。
        if (string.IsNullOrEmpty(path))
        {
            path = item.Path;
        }

        string kind = item.IsFolder ? $"未読 {item.PendingFiles.Count + (long)item.SavedUnreadWithoutDetails}件" : (string.IsNullOrEmpty(item.LastChange) ? "更新" : item.LastChange);
        var root = NewNode(item.Id.ToString("N"),
            new(item.Id, path, null, null, false, item.LastChanged, item.LastModified, kind, item.IsFolder,
                item.IsFolder && item.Recursive), now);

        root.ToolTipText = item.Path;
        __inbox.ShowNodeToolTips = true;
        __inbox.Nodes.Add(root);
        // 個別ファイルの登録には配下の変更ツリーを作らない。
        if (!item.IsFolder)
        {
            return;
        }

        foreach (var change in item.PendingFiles.Values.OrderBy(x => x.Relative, StringComparer.OrdinalIgnoreCase))
        {
            AddChangedFile(root, item.Id, change, now);
        }

        // 旧版の未読にファイル別の内訳がなければ、件数だけを案内行に残す。
        if (item.SavedUnreadWithoutDetails > 0)
        {
            root.Nodes.Add(new TreeNode($"旧版から引き継いだ未読 {item.SavedUnreadWithoutDetails} 件（ファイル別の記録なし）")
            {
                Name = root.Name + "|legacy"
            });
        }
    }

    /// @brief 未読の対象や変更された子ファイルをツリーに追加する。
    private static void AddChangedFile(TreeNode root, Guid id, FileChange change, DateTimeOffset now)
    {
        var pieces = change.Relative.Split(Path.DirectorySeparatorChar);
        // 空の名前や親参照を含む子パスはツリーに追加しない。
        if (pieces.Any(p => p.Length == 0 || p is "." or ".."))
        {
            return;
        }

        var parent = root;
        string relative = "";
        for (int i = 0; i < pieces.Length - 1; i++)
        {
            relative = Path.Combine(relative, pieces[i]);
            var key = root.Name + "|dir|" + relative;
            var branch = parent.Nodes[key];
            // 途中のフォルダ行がまだなければ作成し、同じ枝を重複させない。
            if (branch is null)
            {
                branch = NewNode(key, new(id, pieces[i], relative, null, true, null, null, "—", true), now);
                parent.Nodes.Add(branch);
            }
            parent = branch;
        }
        var kind = change.Kind;
        var file = NewNode(root.Name + "|file|" + change.Relative,
            new(id, pieces[^1], change.Relative, change, false, change.DetectedAt, change.ModifiedAt, kind, false), now);
        file.ToolTipText = change.Relative;
        parent.Nodes.Add(file);
    }

    /// @brief 行情報からツリーノードを生成する。
    private static TreeNode NewNode(string name, InboxRow row, DateTimeOffset now)
    {
        string icon = row.IsFolder ? "folder" : "file";
        var node = new TreeNode { Name = name, Tag = row, ImageKey = icon, SelectedImageKey = icon };
        PaintAge(node, row, now);
        return node;
    }

    /// @brief 階層化したツリーノードを順に列挙する。
    private static IEnumerable<TreeNode> Flatten(TreeNodeCollection nodes)
    {
        foreach (TreeNode node in nodes)
        {
            yield return node;
            foreach (var descendant in Flatten(node.Nodes))
            {
                yield return descendant;
            }
        }
    }

    /// @brief 検知時刻から読みやすい経過時間表示を作る。
    private static void PaintAge(TreeNode node, InboxRow row, DateTimeOffset now)
    {
        // ネイティブのアクセシビリティとツールチップに、描画した列と同じ情報を渡す。
        string label = $"{row.Label}　{row.Kind}　{InboxUpdater(row)}　{InboxAge(row, now)}";
        // 表示ラベルが変わるときだけ更新し、不要な再描画を避ける。
        if (node.Text != label)
        {
            node.Text = label;
        }
    }

    /// @brief 検知時刻から読みやすい経過時間表示を作る。
    internal static string RelativeAge(DateTimeOffset at, DateTimeOffset now)
    {
        var elapsed = now - at;
        // 検知から1分未満なら「たった今」と表示する。
        if (elapsed.TotalMinutes < 1)
        {
            return "たった今";
        }

        // 1時間未満の経過時間は分単位で表示する。
        if (elapsed.TotalHours < 1)
        {
            return $"{(int)elapsed.TotalMinutes}分前";
        }

        // 1日未満の経過時間は時間単位で表示する。
        if (elapsed.TotalDays < 1)
        {
            return $"{(int)elapsed.TotalHours}時間前";
        }

        return $"{(int)elapsed.TotalDays}日前";
    }

    /// @brief 選択行の詳細と既読になる範囲を表示する。
    private void UpdateSelectedDetails()
    {
        // 選択がない場合は、直前の対象の詳細を残さない。
        if (SelectedInbox() is not { } selection)
        {
            __selectedTitle.Text = "項目が選ばれていません";
            __selectedMeta.Text = "左の一覧から変更を選ぶと、時刻や場所を確認できます。";
            __selectedPath.Text = "";
            __selectedPath.Visible = false;
            __selectedScope.Text = "";
            __actionScope.Text = "";
            return;
        }
        var (item, row) = selection;
        var fullPath = row.Relative is { } relative ? Path.Combine(item.Path, relative) : item.Path;
        __selectedTitle.Text = row.Label;
        __selectedPath.Text = fullPath;
        __selectedPath.Visible = true;
        __selectedMeta.Text = row.ModifiedAt is { } modified
            ? $"更新：{Time(modified)}\n検知：{Time(row.DetectedAt)}"
            : row.DetectedAt is { } detected ? $"検知：{Time(detected)}" : "このフォルダ内の未読の変更";
        __selectedScope.Text = row.Change is not null || !item.IsFolder ? "確認済みになる範囲：このファイル" :
            row.IsBranch ? "確認済みになる範囲：このサブフォルダの配下" : "確認済みになる範囲：この監視フォルダの全未読";
        __actionScope.Text = row.IsFolder ? "このフォルダ配下が対象" : "このファイルが対象";
    }

    /// @brief 選択行と対応する監視対象を解決する。
    private (WatchItem Item, InboxRow Row)? SelectedInbox()
    {
        // 案内行など監視対象でない選択は、操作対象として返さない。
        if (__inbox.SelectedNode?.Tag is not InboxRow row)
        {
            return null;
        }

        return __state.Items.FirstOrDefault(x => x.Id == row.Id) is { } item ? (item, row) : null;
    }

    /// @brief 選択した範囲だけを既読にする。
    private void MarkInboxRead()
    {
        // 対象が選択されていなければ未読状態を変更しない。
        if (SelectedInbox() is not { } selection)
        {
            return;
        }

        GetAcknowledgement(selection.Item, selection.Row)();
        Save();
        RefreshRows();
    }

    /// @brief 開く前に確認した未読範囲をキャプチャする。
    private static Action GetAcknowledgement(WatchItem item, InboxRow row)
    {
        // 個別ファイルの行は、その変更だけを既読にする。
        if (row.Change is { } file)
        {
            return () => item.AcknowledgeFile(file);
        }

        var seen = item.LastChanged;
        var unread = item.Unread;
        if (!item.IsFolder)
        {
            return () =>
            {
                if (item.LastChanged == seen && item.Unread == unread)
                {
                    item.Acknowledge();
                }
            };
        }

        // 開く前の未読だけを保持する。別の子に新着があっても、既存の変更は確認できる。
        var prefix = row.IsBranch ? row.Relative + Path.DirectorySeparatorChar : null;
        var snapshot = item.PendingFiles.Values
            .Where(change => prefix is null || change.Relative.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return () =>
        {
            // 旧形式で内訳がない未読は、ルートの未読が変化していないときだけ一括で消す。
            if (prefix is null && item.SavedUnreadWithoutDetails > 0 &&
                item.LastChanged == seen && item.Unread == unread && item.PendingFiles.Count == snapshot.Length &&
                snapshot.All(change => item.PendingFiles.TryGetValue(change.Relative, out var latest) && latest == change))
            {
                item.Acknowledge();
                return;
            }

            foreach (var change in snapshot)
            {
                item.AcknowledgeFile(change);
            }
        };
    }

}
