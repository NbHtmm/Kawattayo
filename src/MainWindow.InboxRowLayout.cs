namespace ShareFileWatch;

internal sealed partial class MainWindow
{
    private readonly record struct InboxRowLayout(Rectangle Toggle, Rectangle Icon, Rectangle Label, Rectangle Read);

    private static void DrawInboxRowSymbols(Graphics graphics, InboxRowLayout row,
        bool folder, bool expanded, Color foreground)
    {
        if (!row.Toggle.IsEmpty)
        {
            // マークを囲む枠が、そのまま押せる範囲を示す。
            ControlPaint.DrawBorder(graphics, row.Toggle, foreground, ButtonBorderStyle.Solid);
            int x = row.Toggle.X + row.Toggle.Width / 2;
            int y = row.Toggle.Y + row.Toggle.Height / 2;
            int arm = Math.Max(1, row.Toggle.Width / 4);
            using var pen = new Pen(foreground, Math.Max(1, row.Toggle.Width / 10f));
            graphics.DrawLine(pen, x - arm, y, x + arm, y);
            if (!expanded)
            {
                graphics.DrawLine(pen, x, y - arm, x, y + arm);
            }
        }

        if (!row.Icon.IsEmpty)
        {
            DrawInboxIcon(graphics, row.Icon, folder);
        }
    }

    private sealed partial class InboxTree
    {
        /// @brief 同じ行のクロームを描画・押下・解放のすべてで同じ座標にする。
        public InboxRowLayout LayoutRow(TreeNode node, Rectangle nativeBounds)
        {
            var cols = LayoutColumns;
            int icon = Math.Min(ScaleForDisplay(this, 16), nativeBounds.Height - 2);
            int marker = Math.Min(ScaleForDisplay(this, 16), nativeBounds.Height - 2);
            int padding = ScaleForDisplay(this, 5);
            int gap = ScaleForDisplay(this, 4);
            int left = Math.Max(padding, Math.Min(padding + node.Level * Indent,
                cols.Change - marker - icon - 3 * gap - Math.Max(48, cols.Change / 2)));
            int centerY = nativeBounds.Top + nativeBounds.Height / 2;
            var toggle = node.Nodes.Count > 0 && marker > 0
                ? new Rectangle(left, centerY - marker / 2, marker, marker)
                : Rectangle.Empty;
            var file = icon > 0
                ? new Rectangle(left + marker + gap, centerY - icon / 2, icon, icon)
                : Rectangle.Empty;
            int label = left + marker + icon + gap * 2;
            var text = new Rectangle(label, nativeBounds.Top, Math.Max(0, cols.Change - label), nativeBounds.Height);
            var read = Rectangle.FromLTRB(cols.Action + 2, nativeBounds.Top + 1, cols.End - 2, nativeBounds.Bottom - 1);
            return new(toggle, file, text, read);
        }
    }
}
