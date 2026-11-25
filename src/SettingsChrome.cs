namespace ShareFileWatch;

// タブのラベルだけでなく余白も描き、ネイティブの白い帯を残さない。
/// @brief 設定画面のタブ・グループの描画を管理する。
internal sealed class SettingsTabs : TabControl
{
    /// @brief 設定画面のタブ・グループの描画を管理する。
    public SettingsTabs()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Dock = DockStyle.Fill;
        Padding = new Point(18, 6);
    }
    /// @brief 選択タブが変わったとき見出しを描き直す。
    protected override void OnSelectedIndexChanged(EventArgs e)
    {
        base.OnSelectedIndexChanged(e);
        Invalidate();
    }
    /// @brief コントロールの状態に合わせて内容を描画する。
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(WatchTheme.Window);
        for (int i = 0; i < TabCount; i++)
        {
            var r = GetTabRect(i);
            bool selected = i == SelectedIndex;
            using var brush = new SolidBrush(selected ? WatchTheme.Surface : WatchTheme.Window);
            e.Graphics.FillRectangle(brush, r);
            TextRenderer.DrawText(e.Graphics, TabPages[i].Text, Font, r, selected ? WatchTheme.Ink : WatchTheme.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            // 選択中のタブにはアクセント色の線を引く。
            if (selected)
            {
                using var pen = new Pen(WatchTheme.Accent, 2);
                e.Graphics.DrawLine(pen, r.Left, r.Bottom - 2, r.Right, r.Bottom - 2);
            }
            // キーボード操作でフォーカス表示が必要な場合だけ選択タブを枠で示す。
            if (selected && Focused && ShowFocusCues)
            {
                ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(r, -3, -3), WatchTheme.Ink, WatchTheme.Surface);
            }
        }
    }
}

/// @brief 設定画面のタブ・グループの描画を管理する。
internal sealed class SettingsGroup : GroupBox
{
    /// @brief 設定画面のタブ・グループの描画を管理する。
    public SettingsGroup(string title, Control content, bool fill = false)
    {
        Text = title;
        Tag = WatchTheme.Role.Surface;
        Dock = DockStyle.Fill;
        Margin = new Padding(0, 0, 0, 8);
        Padding = new Padding(12, 6, 12, 8);
        AutoSize = !fill;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        content.Dock = fill ? DockStyle.Fill : DockStyle.Top;
        Controls.Add(content);
    }
    /// @brief コントロールの状態に合わせて内容を描画する。
    protected override void OnPaint(PaintEventArgs e)
    {
        var size = TextRenderer.MeasureText(e.Graphics, Text, Font);
        int y = size.Height / 2;
        using var pen = new Pen(WatchTheme.Border);
        e.Graphics.DrawRectangle(pen, 0, y, Math.Max(0, Width - 1), Math.Max(0, Height - y - 1));
        TextRenderer.DrawText(e.Graphics, Text, Font, new Point(10, 0), WatchTheme.Ink, BackColor);
    }
}
