namespace ShareFileWatch;

// 画面共通の余白・色・部品。ハイコントラスト時はWindowsの配色を使う。
/// @brief 画面間で共有する配色・描画・コントロール生成。
internal static class WatchTheme
{
    /// @brief 現在のテーマとシステム設定に合わせた表示値。
    private static bool HighContrast => SystemInformation.HighContrast;
    public static bool DarkMode
    {
        get; set;
    } //!< 永続化する操作・監視・表示の設定。
    /// @brief 現在のテーマとシステム設定に合わせた表示値。
    public static bool Dark => DarkMode && !HighContrast;
    /// @brief 現在のテーマとシステム設定に合わせた表示値。
    public static Color Window => HighContrast ? SystemColors.Window : Dark ? Color.FromArgb(28, 31, 36) : Color.FromArgb(240, 244, 249);
    /// @brief 現在のテーマとシステム設定に合わせた表示値。
    public static Color Surface => HighContrast ? SystemColors.Window : Dark ? Color.FromArgb(39, 43, 50) : Color.White;
    /// @brief 現在のテーマとシステム設定に合わせた表示値。
    public static Color Ink => HighContrast ? SystemColors.WindowText : Dark ? Color.WhiteSmoke : Color.FromArgb(24, 39, 57);
    /// @brief 現在のテーマとシステム設定に合わせた表示値。
    public static Color Muted => HighContrast ? SystemColors.WindowText : Dark ? Color.LightGray : Color.FromArgb(72, 91, 112);
    /// @brief 現在のテーマとシステム設定に合わせた表示値。
    public static Color Accent => HighContrast ? SystemColors.Highlight : Dark ? Color.LightSkyBlue : Color.FromArgb(22, 75, 145);
    /// @brief 現在のテーマとシステム設定に合わせた表示値。
    public static Color Border => HighContrast ? SystemColors.WindowText : Dark ? Color.SlateGray : Color.FromArgb(190, 205, 221);
    /// @brief 未確認の変更件数、またはそれを示すテーマ色。
    public static Color Unread => HighContrast ? SystemColors.Window : Dark ? Color.FromArgb(48, 70, 96) : Color.FromArgb(234, 243, 254);
    /// @brief 現在のテーマとシステム設定に合わせた表示値。
    public static Color HeaderColor => HighContrast ? Surface : Dark ? Window : Color.FromArgb(231, 238, 247);
    /// @brief 現在のテーマとシステム設定に合わせた表示値。
    public static Color FailureSurface => HighContrast ? SystemColors.Window : Dark ? Color.FromArgb(80, 43, 42) : Color.MistyRose;
    /// @brief 現在のテーマとシステム設定に合わせた表示値。
    public static Color FailureInk => HighContrast ? SystemColors.WindowText : Dark ? Color.FromArgb(255, 216, 207) : Color.Maroon;
    /// @brief 配色を選ぶためのコントロールの役割。
    internal enum Role
    {
        Surface, Muted, Badge, Header, Primary, Window
    }

    // 色値ではなく用途から再計算するため、開いた画面を作り直さず切り替えられる。
    /// @brief 現在のテーマとハイコントラスト設定を反映する。
    public static void Apply(Control c)
    {
        // 背景色は画面での役割を優先し、指定がなければコントロールの種類で選ぶ。
        c.BackColor = c.Tag switch
        {
            Role.Surface => Surface,
            Role.Badge => Unread,
            Role.Header => HeaderColor,
            Role.Window => Window,
            // 役割指定のない入力欄と一覧は共通の面色を使い、その他は親の背景になじませる。
            _ => c switch { Form => Window, ListView or TreeView or TextBoxBase or NumericUpDown or ToolStrip => Surface, _ => c.Parent?.BackColor ?? Window }
        };
        c.ForeColor = Equals(c.Tag, Role.Muted) ? Muted : Ink;
        // ボタンには通常・強調・無効状態に合わせた配色を適用する。
        if (c is Button b)
        {
            StyleButton(b);
        }

        // ツールストリップでは子項目にもテーマの文字色を反映する。
        if (c is ToolStrip strip)
        {
            strip.RenderMode = ToolStripRenderMode.System;
            foreach (ToolStripItem item in strip.Items)
            {
                item.ForeColor = Ink;
                // ステータスラベルは通常の文字色とは別にリンク色も設定する。
                if (item is ToolStripStatusLabel label)
                {
                    label.LinkColor = Accent;
                }
            }
        }
        foreach (Control child in c.Controls)
        {
            Apply(child);
        }

        c.Invalidate();
    }

    /// @brief 現在のテーマとハイコントラスト設定を反映する。
    private static void StyleButton(Button b)
    {
        // ハイコントラスト時はアプリ独自の配色を使わず、システムのボタン描画に任せる。
        if (HighContrast)
        {
            b.FlatStyle = FlatStyle.System;
            return;
        }
        bool primary = Equals(b.Tag, Role.Primary);
        b.FlatStyle = FlatStyle.Flat;
        b.UseVisualStyleBackColor = false;
        b.BackColor = !b.Enabled ? Window : primary ? Accent : Surface;
        b.ForeColor = !b.Enabled ? Muted : primary ? (Dark ? Window : Color.White) : Ink;
        b.FlatAppearance.BorderColor = b.Enabled && primary ? Accent : Border;
    }

    /// @brief UI フォントの太字を生成する。
    public static Font Bold(float size) => new("Yu Gothic UI", size, FontStyle.Bold);

    /// @brief 文字フォントに依存しない円形の更新矢印を描画する。
    public static void DrawRefreshGlyph(Button button, Graphics g)
    {
        // フォントの字形やベースラインに依存せず、ボタン中央の正方形に描く。
        float d = Math.Min(button.ClientSize.Width, button.ClientSize.Height) * 0.50f;
        // 矢印を描く幅が確保できなければ描画を省く。
        if (d < 1)
        {
            return;
        }

        var saved = g.Save();
        try
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TranslateTransform((button.ClientSize.Width - d) / 2, (button.ClientSize.Height - d) / 2);
            g.ScaleTransform(d / 20f, d / 20f);
            using var pen = new Pen(button.Enabled ? button.ForeColor : HighContrast ? SystemColors.GrayText : Muted, 2.3f) { LineJoin = System.Drawing.Drawing2D.LineJoin.Round };
            g.DrawArc(pen, 2, 2, 16, 16, 8, 318);
            g.DrawLines(pen, new PointF[] { new(11, 6), new(17, 6), new(17, 0) });
        }
        finally { g.Restore(saved); }
    }

    // WinForms の標準描画は無効ボタンの文字色を上書きするため、暗色時だけ明示的に描く。
    /// @brief 無効時も文字が読めるように描画するボタン。
    private sealed class ReadableButton : Button
    {
        /// @brief コントロールの状態に合わせて内容を描画する。
        protected override void OnPaint(PaintEventArgs e)
        {
            // 有効なボタンとライト表示のボタンは標準描画に任せる。
            if (Enabled || !Dark)
            {
                base.OnPaint(e);
                return;
            }
            e.Graphics.Clear(Window);
            ControlPaint.DrawBorder(e.Graphics, ClientRectangle, Border, ButtonBorderStyle.Solid);
            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            // 無効ボタンの独自描画でも、更新ボタンには円形矢印を表示する。
            if (Name == "scanNow")
            {
                DrawRefreshGlyph(this, e.Graphics);
            }
        }
    }

    /// @brief データ行と区別できる見出しと選択表示を適用する。
    public static void StyleListHeader(ListView list)
    {
        list.OwnerDraw = true;
        list.DrawItem += (_, e) => e.DrawDefault = !Dark;
        list.DrawSubItem += (_, e) =>
        {
            // ライト表示または行・セル情報がない場合は、一覧の標準描画を使う。
            if (!Dark || e.Item is not { } row || e.SubItem is not { } cell)
            {
                e.DrawDefault = true;
                return;
            }
            Color bg = row.Selected ? Color.FromArgb(58, 85, 115) : row.BackColor;
            using var brush = new SolidBrush(bg);
            e.Graphics.FillRectangle(brush, e.Bounds);
            TextRenderer.DrawText(e.Graphics, cell.Text, list.Font, Rectangle.Inflate(e.Bounds, -4, 0), row.Selected ? Ink : row.ForeColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
            // キーボードフォーカスがある行の先頭セルだけにフォーカス枠を描く。
            if (row.Focused && list.Focused && e.ColumnIndex == 0)
            {
                ControlPaint.DrawFocusRectangle(e.Graphics, e.Bounds, Ink, bg);
            }
        };
        list.DrawColumnHeader += (_, e) =>
        {
            // ハイコントラスト時の列見出しはシステム設定に従って描画する。
            if (HighContrast)
            {
                e.DrawDefault = true;
                return;
            }
            using var fill = new SolidBrush(Dark ? Color.FromArgb(77, 77, 83) : Color.FromArgb(218, 222, 228));
            e.Graphics.FillRectangle(fill, e.Bounds);
            using var rule = new Pen(Dark ? Color.Silver : Color.SlateGray);
            e.Graphics.DrawLine(rule, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
            e.Graphics.DrawLine(rule, e.Bounds.Right - 1, e.Bounds.Top + 3, e.Bounds.Right - 1, e.Bounds.Bottom - 3);
            var bounds = Rectangle.Inflate(e.Bounds, -4, 0);
            using var font = new Font(list.Font, FontStyle.Bold);
            TextRenderer.DrawText(e.Graphics, e.Header?.Text, font, bounds, Ink,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
        };
    }

    /// @brief 画面共通の寸法・配色でコントロールを構築する。
    public static TableLayoutPanel Layout(int rows, int columns = 1) => new()
    {
        Dock = DockStyle.Fill,
        RowCount = rows,
        ColumnCount = columns,
        Margin = Padding.Empty
    };

    /// @brief 画面共通の寸法・配色でコントロールを構築する。
    public static TableLayoutPanel Card(int rows) => new()
    {
        Dock = DockStyle.Fill,
        RowCount = rows,
        ColumnCount = 1,
        BackColor = Surface,
        Tag = Role.Surface,
        ForeColor = Ink,
        Padding = new Padding(14),
        Margin = new Padding(0, 0, 0, 10)
    };

    /// @brief 画面共通の寸法・配色でコントロールを構築する。
    public static Label Label(string text, float size = 10, bool bold = false, bool muted = false) => new()
    {
        Text = text,
        AutoSize = true,
        Font = bold ? Bold(size) : new Font("Yu Gothic UI", size),
        ForeColor = muted ? Muted : Ink,
        Tag = muted ? Role.Muted : null,
        Margin = new Padding(0, 0, 0, 5)
    };

    /// @brief 画面共通の寸法・配色でコントロールを構築する。
    public static Button Button(string text, Action click, bool primary = false, string? name = null)
    {
        var button = new ReadableButton
        {
            Text = text,
            Name = name ?? "",
            AutoSize = true,
            Height = 35,
            MinimumSize = new Size(76, 35),
            Padding = new Padding(8, 0, 8, 0),
            Margin = new Padding(0, 0, 6, 5)
        };
        // 主要な操作ボタンだけにアクセント配色の役割を付ける。
        if (primary)
        {
            button.Tag = Role.Primary;
        }

        StyleButton(button);
        button.EnabledChanged += (_, _) => StyleButton(button);
        button.Click += (_, _) => click();
        return button;
    }

    /// @brief 対象に対応する操作をまとめる。
    public static FlowLayoutPanel Actions(params Control[] controls)
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        row.Controls.AddRange(controls);
        return row;
    }

    /// @brief 画面共通の寸法・配色でコントロールを構築する。
    public static FlowLayoutPanel Stack(params Control[] controls)
    {
        var stack = new FlowLayoutPanel
        {
            AutoSize = true,
            WrapContents = false,
            FlowDirection = FlowDirection.TopDown,
            Margin = Padding.Empty
        };
        stack.Controls.AddRange(controls);
        return stack;
    }

    /// @brief 画面共通の寸法・配色でコントロールを構築する。
    public static Control Header(string title, string subtitle, Control actions)
    {
        var header = Layout(1, 2);
        header.Dock = DockStyle.Top;
        header.AutoSize = true;
        header.BackColor = Surface;
        header.Tag = Role.Surface;
        header.Padding = new Padding(18, 12, 12, 10);
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.Controls.Add(Stack(Label(subtitle, 9, bold: true, muted: true), Label(title, 18, bold: true)), 0, 0);
        actions.Anchor = AnchorStyles.Right;
        header.Controls.Add(actions, 1, 0);
        return header;
    }
}
