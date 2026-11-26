namespace ShareFileWatch;

/// @brief 受信箱と監視のライフサイクルを管理するメイン画面。
internal sealed partial class MainWindow
{
    private static readonly string[] s_s_inboxColumnTitles = ["ファイル・フォルダ", "変更", "最終更新者", "最新の検知", "操作"]; //!< 通知一覧の列見出し・描画に使う固定値。
    private const TextFormatFlags ColumnTextFlags = TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter |
        TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.PreserveGraphicsClipping; //!< 通知一覧の列見出し・描画に使う固定値。

    // 更新者の記録は共有ファイルの属性にない。所有者やPC名では代用しない。
    /// @brief 行から更新者・経過時間の表示文字列を得る。
    private static string InboxUpdater(InboxRow row) =>
        row.IsFolder || row.Kind.StartsWith("消失", StringComparison.Ordinal) ? "—" : "取得不可";

    /// @brief 行から更新者・経過時間の表示文字列を得る。
    private static string InboxAge(InboxRow row, DateTimeOffset now) =>
        row.DetectedAt is { } detected ? RelativeAge(detected, now) : "—";

    /// @brief 描画とクリック判定で共有する列の境界。
    private readonly record struct InboxColumns(int Change, int Updater, int Detected, int Action, int End);

    /// @brief 描画とクリック判定で共有する列の境界。
    private static InboxColumns GetInboxColumns(int width, int dpi, float textScale = 1)
    {
        // 狭い画面でも右側の列が名前欄を押し出さないよう、補助列を比例して縮める。
        // 行・見出し・クリック判定はいずれも同じ境界を参照する。
        const float secondaryWidth = 111 + 117 + 105 + 84;
        int end = Math.Max(1, width);
        float scale = Math.Min(dpi / 96f * textScale, end * 0.73f / secondaryWidth);
        int action = end - (int)Math.Round(84 * scale);
        int detected = end - (int)Math.Round((84 + 105) * scale);
        int updater = end - (int)Math.Round((84 + 105 + 117) * scale);
        return new(end - (int)Math.Round(secondaryWidth * scale), updater, detected, action, end);
    }

    private sealed partial class InboxTree
    {
        public InboxColumns LayoutColumns => GetInboxColumns(ClientSize.Width, DeviceDpi,
            Font.SizeInPoints / DefaultDisplayPoints);

        public event EventHandler? ColumnsChanged;

        protected override void OnClientSizeChanged(EventArgs e)
        {
            base.OnClientSizeChanged(e);
            ColumnsChanged?.Invoke(this, EventArgs.Empty);
            Invalidate();
        }
    }

    /// @brief 通知行の内容をテーマと列幅に合わせて描画する。
    private static void DrawInboxCell(Graphics graphics, Font font, string value, Color color,
        int left, int right, Rectangle row)
    {
        // 幅が足りないセルは隣の列へはみ出さないよう描画しない。
        if (right - left < 8)
        {
            return;
        }

        var cell = Rectangle.FromLTRB(left + 4, row.Top, right - 4, row.Bottom);
        TextRenderer.DrawText(graphics, value, font, cell, color, ColumnTextFlags);
    }

    /// @brief 通知行の内容をテーマと列幅に合わせて描画する。
    private void DrawInboxRow(object? sender, DrawTreeNodeEventArgs e)
    {
        // 通知行でない場合や描画幅がない場合は標準描画に任せる。
        if (e.Node?.Tag is not InboxRow row || e.Bounds.Width <= 0)
        {
            e.DrawDefault = true;
            return;
        }

        var cols = __inbox.LayoutColumns;
        bool selected = e.Node == __inbox.SelectedNode;
        bool highContrast = SystemInformation.HighContrast;
        Color foreground = selected && highContrast ? SystemColors.HighlightText : WatchTheme.Ink;
        Color background = selected
            ? (highContrast ? SystemColors.Highlight : WatchTheme.Unread)
            : e.Bounds.Top / Math.Max(1, __inbox.ItemHeight) % 2 == 0 || highContrast
                ? WatchTheme.Surface : WatchTheme.Dark ? WatchTheme.Window : Color.FromArgb(246, 248, 251);

        // アイコン・開閉マーク・ボタンの四角形を入力処理と共有する。
        var layout = __inbox.LayoutRow(e.Node, e.Bounds);
        var rowBounds = new Rectangle(0, e.Bounds.Top, cols.End, e.Bounds.Height);
        using var brush = new SolidBrush(background);
        e.Graphics.FillRectangle(brush, rowBounds);
        DrawInboxRowSymbols(e.Graphics, layout, row.IsFolder, e.Node.IsExpanded, foreground);

        var font = __inbox.Font;
        DrawInboxCell(e.Graphics, font, row.Label, foreground, layout.Label.Left - 4, cols.Change, e.Bounds);
        // 選択色とハイコントラストを優先し、それ以外は追加・消失などの変更種別で色を分ける。
        Color changeColor = highContrast || selected ? foreground : row.Kind switch
        {
            var value when value.StartsWith("追加", StringComparison.Ordinal) => WatchTheme.Dark ? Color.LightGreen : Color.FromArgb(25, 99, 68),
            var value when value.StartsWith("消失", StringComparison.Ordinal) => WatchTheme.Dark ? Color.LightPink : Color.FromArgb(153, 44, 66),
            _ => WatchTheme.Accent
        };
        DrawInboxCell(e.Graphics, font, row.Kind, changeColor, cols.Change, cols.Updater, e.Bounds);
        DrawInboxCell(e.Graphics, font, InboxUpdater(row), foreground, cols.Updater, cols.Detected, e.Bounds);
        DrawInboxCell(e.Graphics, font, InboxAge(row, DateTimeOffset.Now), foreground, cols.Detected, cols.Action, e.Bounds);
        var button = layout.Read;
        if (button.Width > 0 && button.Height > 0)
        {
            ControlPaint.DrawBorder(e.Graphics, button, WatchTheme.Border, ButtonBorderStyle.Solid);
            TextRenderer.DrawText(e.Graphics, "確認済み", font, button, foreground, ColumnTextFlags | TextFormatFlags.HorizontalCenter);
        }

        // 選択行にキーボードフォーカスがある場合だけ、フォーカス枠を描く。
        if (selected && __inbox.Focused)
        {
            ControlPaint.DrawFocusRectangle(e.Graphics, rowBounds, foreground, background);
        }
    }

    /// @brief 通知行の内容をテーマと列幅に合わせて描画する。
    private static void DrawInboxIcon(Graphics graphics, Rectangle bounds, bool folder)
    {
        var state = graphics.Save();
        try
        {
            graphics.TranslateTransform(bounds.Left, bounds.Top);
            graphics.ScaleTransform(bounds.Width / 16f, bounds.Height / 16f);
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            DrawInboxIconShape(graphics, folder);
        }
        finally { graphics.Restore(state); }
    }

    private static void DrawInboxIconShape(Graphics graphics, bool folder)
    {
        Color outline = SystemInformation.HighContrast ? SystemColors.WindowText :
            folder ? Color.FromArgb(138, 92, 26) : Color.FromArgb(65, 110, 164);
        using var pen = new Pen(outline);
        using var fill = new SolidBrush(SystemInformation.HighContrast ? SystemColors.Window :
            folder ? Color.FromArgb(250, 194, 89) : Color.FromArgb(222, 237, 251));
        Point[] points = folder
            ? [new(1, 4), new(6, 4), new(8, 6), new(14, 6), new(14, 13), new(1, 13)]
            : [new(3, 2), new(10, 2), new(13, 5), new(13, 14), new(3, 14)];
        graphics.FillPolygon(fill, points);
        graphics.DrawPolygon(pen, points);
        // ファイルのアイコンにだけ文書を表す線を引く。
        if (!folder)
        {
            graphics.DrawLine(pen, 5, 8, 11, 8);
            graphics.DrawLine(pen, 5, 11, 11, 11);
        }
    }

    /// @brief 通知行と同じ列境界を使うヘッダー。
    private sealed class InboxColumnHeader : Control
    {
        private readonly InboxTree __tree; //!< 列幅を共有するツリー。
        private readonly ViewSort __sort; //!< 選択中の並び順。
        private readonly Action<int> __toggleSort; //!< クリックされた列の並び替え。

        /// @brief 通知行と同じ列境界を使うヘッダー。
        public InboxColumnHeader(InboxTree tree, ViewSort sort, Action<int> toggleSort)
        {
            __tree = tree;
            __sort = sort;
            __toggleSort = toggleSort;
            Height = 25;
            Margin = Padding.Empty;
            Dock = DockStyle.Top;
            Tag = WatchTheme.Role.Header;
            BackColor = WatchTheme.HeaderColor;
            ForeColor = WatchTheme.Ink;
            AccessibleName = string.Join("、", s_s_inboxColumnTitles);
            AccessibleRole = AccessibleRole.ColumnHeader;
            SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer, true);
        }

        /// @brief 描画と同じ列境界でクリックを判定する。操作列はデータを持たないので対象外。
        private int SortColumnAt(int x)
        {
            var cols = __tree.LayoutColumns;
            if (x < 0 || x >= cols.Action)
            {
                return -1;
            }
            return x < cols.Change ? 0 : x < cols.Updater ? 1 : x < cols.Detected ? 2 : 3;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            Cursor = SortColumnAt(e.X) >= 0 ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            HandleSortClick(e);
        }

        // GUI 試験では、他のウィンドウの重なりに依存せず列のヒット判定とクリック処理を通す。
        internal void HandleSortClick(MouseEventArgs e)
        {
            int column = SortColumnAt(e.X);
            if (e.Button == MouseButtons.Left && column >= 0)
            {
                __toggleSort(column);
            }
        }

        /// @brief コントロールの状態に合わせて内容を描画する。
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var cols = __tree.LayoutColumns;
            int[] boundaries = [Math.Min(8, cols.Change), cols.Change, cols.Updater, cols.Detected, cols.Action, cols.End];
            using var separator = new Pen(WatchTheme.Border);
            for (int i = 0; i < s_s_inboxColumnTitles.Length; i++)
            {
                // 先頭列には左隣がないため、区切り線は2列目以降に引く。
                if (i > 0)
                {
                    e.Graphics.DrawLine(separator, boundaries[i], 0, boundaries[i], Height);
                }

                DrawInboxCell(e.Graphics, Font, __sort.Heading(s_s_inboxColumnTitles[i], i), ForeColor,
                    boundaries[i], boundaries[i + 1], ClientRectangle);
            }
        }
    }
}
