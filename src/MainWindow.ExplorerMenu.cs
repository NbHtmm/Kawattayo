using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ShareFileWatch;

/// @brief 受信箱と監視のライフサイクルを管理するメイン画面。
internal sealed partial class MainWindow
{
    private Action<string, ContextMenuStrip>? __verifyShellMenu = null; //!< 実際の OS 操作に代えて回帰試験が観測するコールバック。
    /// @brief 対象の種類に合う操作だけをメニューに表示する。
    private void ShowInboxShellMenu(TreeNode node, Point screen)
    {
        // 対象パスを特定できない案内行には操作メニューを出さない。
        if (ResolveInboxMenuPath(node) is not { } path || node.Tag is not InboxRow row)
        {
            return;
        }

        __inbox.SelectForMenu(node);
        var nodes = __inbox.Selection();
        var menu = new ContextMenuStrip();
        var item = __state.Items.Single(x => x.Id == row.Id);
        var acknowledge = GetAcknowledgement(item, row);
        if (nodes.Length == 1)
        {
        menu.Items.Add("開く", null, async (_, _) => await OpenAndConfirm(item, path, acknowledge, row.IsFolder ? null : __verifyFileOpen ?? WindowsShell.OpenFile));
        // ファイル行で親を特定できるときだけ「親フォルダを開く」を追加する。
        if (!row.IsFolder && Path.GetDirectoryName(path) is { } parent)
        {
            menu.Items.Add("親フォルダを開く", null, async (_, _) => await OpenAndConfirm(item, parent, acknowledge));
        }

        }
        var actions = CaptureRows(nodes);
        menu.Items.Add("確認済みにする", null, (_, _) => ApplyAcknowledgements(actions));
        menu.Items.Add("パスをコピー", null, (_, _) => CopyText(string.Join(Environment.NewLine, nodes.Select(ResolveInboxMenuPath).Distinct(StringComparer.OrdinalIgnoreCase))));
        if (nodes.All(n => n.Parent == null))
        {
            var ids = nodes.Select(n => ((InboxRow)n.Tag!).Id).ToHashSet();
            var targets = __state.Items.Where(x => ids.Contains(x.Id)).ToArray();
            menu.Items.Add("登録解除", null, (_, _) => Unregister(targets));
        }
        var paths = nodes.Select(ResolveInboxMenuPath).OfType<string>().ToArray();
        menu.Items.Add("コピー", null, async (_, _) => await CopyInboxTarget(paths));
        // テスト用の受け取り先があれば、実際に表示せずメニュー内容を渡す。
        if (__verifyShellMenu is { } verify)
        {
            using (menu)
            {
                verify(path, menu);
            }

            return;
        }
        menu.Closed += (_, _) => BeginInvoke(new Action(menu.Dispose));
        menu.Show(screen);
    }

    /// @brief 選択文字列をクリップボードへ転送する。
    private static void CopyText(string text)
    {
        try
        {
            // 空の選択で利用者のクリップボードを上書きしない。
            if (text.Length > 0)
            {
                Clipboard.SetDataObject(text, true, 5, 50);
            }
        }
        catch (ExternalException) { MessageBox.Show("クリップボードを使用できません。もう一度コピーしてください。", AppDefaults.AppName); }
    }
    private Func<string, bool>? __verifyFileOpen = null; //!< 実際の OS 操作に代えて回帰試験が観測するコールバック。

    /// @brief 行を実際の対象に解決して既定の操作で開く。
    private async Task OpenInboxItem(TreeNode node)
    {
        // 有効なパスを持たない行では既定の「開く」操作を行わない。
        if (ResolveInboxMenuPath(node) is not { } path)
        {
            return;
        }

        __inbox.SelectedNode = node;
        // 登録対象と表示行を対応付けられない場合は、開いたり既読にしたりしない。
        if (SelectedInbox() is not { } selection)
        {
            return;
        }

        var (item, row) = selection;
        bool isFile = !row.IsFolder;
        await OpenAndConfirm(item, path, GetAcknowledgement(item, row), isFile ? __verifyFileOpen ?? WindowsShell.OpenFile : null);
    }


    /// @brief ツリーの行から操作対象の絶対パスを求める。
    private string? ResolveInboxMenuPath(TreeNode node)
    {
        // 登録解除済みの対象や案内用の行からは操作パスを生成しない。
        if (node.Tag is not InboxRow row || __state.Items.FirstOrDefault(x => x.Id == row.Id) is not { } item)
        {
            return null;
        }

        // 登録のルート行は、子パスを連結せず登録パスをそのまま使う。
        if (row.Relative is not { } relative || relative == ".")
        {
            return item.Path;
        }

        try
        {
            // キャッシュの相対パスが壊れていても監視フォルダの外には出ない。
            var child = Path.GetFullPath(Path.Combine(item.Path, relative));
            var prefix = Path.EndsInDirectorySeparator(item.Path) ? item.Path : item.Path + Path.DirectorySeparatorChar;
            return child.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? child : null;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException) { return null; }
    }

    /// @brief 通知ツリーの入力と行アクションを管理する。
    private sealed partial class InboxTree : TreeView
    {
        // 全列の位置は幅に依存する。ネイティブツリーの「増えた領域だけ」の再描画では足りない。
        /// @brief サイズ変更後の列配置を再描画する。
        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            Invalidate();
        }
        public event Action<TreeNode, Point>? ShellMenuRequested; //!< 対象行に対する利用者の操作を親画面に通知する。
        public event Action<TreeNode>? DefaultOpenRequested; //!< 対象行に対する利用者の操作を親画面に通知する。
        public event Action<TreeNode>? ReadRequested; //!< 対象行に対する利用者の操作を親画面に通知する。
        private enum RowAction { None, Read, Toggle }
        private (TreeNode Node, RowAction Action)? __pressedAction; //!< 押下時の行と操作。

        // 列は画面内に収めるため、長いファイル名で横スクロールを起こさない。
        protected override CreateParams CreateParams
        {
            get
            {
                var parameters = base.CreateParams;
                parameters.Style |= 0x8000; // TVS_NOHSCROLL
                return parameters;
            }
        } //!< ネイティブコントロールの生成スタイルを指定する。

        /// @brief キーボードや Windows メッセージを対象行の操作に変換する。
        protected override void OnKeyDown(KeyEventArgs e)
        {
            // 選択行で Space が押されたら、対象を開かず既読にする。
            if (e.KeyData == Keys.Space && SelectedNode is { } read)
            {
                e.SuppressKeyPress = true;
                ReadRequested?.Invoke(read);
                return;
            }
            // 修飾キーなしの Enter は選択行の既定の「開く」操作に割り当てる。
            if (e.KeyCode == Keys.Enter && e.Modifiers == Keys.None && SelectedNode is { } selected)
            {
                e.SuppressKeyPress = true;
                DefaultOpenRequested?.Invoke(selected);
            }
            base.OnKeyDown(e);
        }

        /// @brief キーボードや Windows メッセージを対象行の操作に変換する。
        protected override void WndProc(ref Message message)
        {
            // 独自のボタン入力を先に処理し、同じ操作を Windows 側で重ねて扱わない。
            if (message.Msg is 0x201 or 0x202 or 0x203)
            {
                int p = unchecked((int)message.LParam);
                var pt = new Point((short)p, (short)(p >> 16));
                if (message.Msg == 0x201 && GetInboxRowAt(pt) is { } hit &&
                    !LayoutRow(hit, hit.Bounds).Toggle.Contains(pt) && !LayoutRow(hit, hit.Bounds).Read.Contains(pt))
                {
                    Focus(); SelectGesture(hit, ModifierKeys); return;
                }
                if (HandleRowAction(message.Msg, pt))
                {
                    message.Result = 0;
                    return;
                }
            }
            const int contextMenu = 0x7b;
            // メニュー要求を処理できる場合は、入力元から対象行を求める。
            if (message.Msg == contextMenu && ShellMenuRequested is { } show)
            {
                int packed = unchecked((int)message.LParam);
                // Shift+F10 やメニューキーの座標は (-1, -1)。その場合は選択行を起点にする。
                // 座標が -1 の要求はキーボード操作なので、マウス位置を使わない。
                if (packed == -1)
                {
                    // キーボードからの要求では選択行があるときだけメニューを表示する。
                    if (SelectedNode is { } selected)
                    {
                        var bounds = selected.Bounds;
                        show(selected, PointToScreen(new Point(bounds.Left, bounds.Bottom)));
                    }
                }
                // マウスからの要求では、クリックした画面座標をツリー座標に変換する。
                else
                {
                    var screen = new Point((short)(packed & 0xffff), (short)(packed >> 16));
                    var client = PointToClient(screen);
                    // ツリー内で実際に行に当たった場合だけ、その行のメニューを表示する。
                    if (ClientRectangle.Contains(client) && GetNodeAt(client) is { } hit)
                    {
                        show(hit, screen);
                    }
                }
                message.Result = 0;
                return;
            }
            const int leftDoubleClick = 0x203;
            // 通常の左ダブルクリックは、開く対象に当たったかを判定する。
            if (message.Msg == leftDoubleClick)
            {
                int packed = unchecked((int)message.LParam);
                var point = new Point((short)packed, (short)(packed >> 16));
                if (GetInboxRowAt(point) is { } node)
                {
                    var row = LayoutRow(node, node.Bounds);
                    if (row.Icon.Contains(point) || row.Label.Contains(point))
                    {
                        DefaultOpenRequested?.Invoke(node);
                    }

                    // 他列をクリックしても、ネイティブ動作で意図せず開閉しない。
                    message.Result = 0;
                    return;
                }
            }
            base.WndProc(ref message);
        }

        private TreeNode? GetInboxRowAt(Point point) =>
            ClientRectangle.Contains(point) && GetNodeAt(point) is { Tag: InboxRow } node &&
            point.Y >= node.Bounds.Top && point.Y < node.Bounds.Bottom ? node : null;

        private bool HandleRowAction(int message, Point point)
        {
            const int down = 0x201, up = 0x202;
            var node = GetInboxRowAt(point);
            var action = RowAction.None;
            if (node is not null)
            {
                var row = LayoutRow(node, node.Bounds);
                action = row.Read.Contains(point) ? RowAction.Read :
                    row.Toggle.Contains(point) ? RowAction.Toggle : RowAction.None;
            }

            if (message == down && node is not null && action != RowAction.None)
            {
                __pressedAction = (node, action);
                Capture = true;
                return true;
            }

            if (message == up && __pressedAction is { } pressed)
            {
                __pressedAction = null;
                Capture = false;
                if (node == pressed.Node && action == pressed.Action)
                {
                    if (action == RowAction.Read)
                    {
                        ReadRequested?.Invoke(pressed.Node);
                    }
                    else
                    {
                        if (pressed.Node.IsExpanded) pressed.Node.Collapse(ignoreChildren: true);
                        else pressed.Node.Expand();
                        Invalidate();
                    }
                }
                return true;
            }

            return action != RowAction.None;
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (!Capture) __pressedAction = null;
        }
    }
}
