using System.Runtime.CompilerServices;

namespace ShareFileWatch;

internal sealed partial class MainWindow
{
    private const int DefaultDisplayPoints = 10;
    private const int MinimumDisplayPoints = 9;
    private const int MaximumDisplayPoints = 22;
    private readonly Dictionary<(float, FontStyle), Font> __displayFonts = new();
    private readonly ConditionalWeakTable<Label, DisplayLabelStyle> __displayLabels = new();
    private readonly ConditionalWeakTable<ListView, DisplayListScale> __displayLists = new();

    private sealed record DisplayLabelStyle(float Offset, FontStyle Style);
    private sealed class DisplayListScale
    {
        public int Points = DefaultDisplayPoints;
    }

    /// @brief 更新一覧や設定の文字をポイント単位で調整し、変更をその場で適用する。
    private Control BuildDisplaySizeControl()
    {
        const string caption = "文字サイズ（pt）";
        var points = new NumericUpDown
        {
            Name = "displayTextSize",
            AccessibleName = caption,
            Minimum = MinimumDisplayPoints,
            Maximum = MaximumDisplayPoints,
            Width = 76,
            Value = Math.Clamp(__state.TextSizePoints, MinimumDisplayPoints, MaximumDisplayPoints)
        };
        points.ValueChanged += (_, _) =>
        {
            __state.TextSizePoints = (int)points.Value;
            ApplyDisplayTypography();
            Save();
        };
        var label = WatchTheme.Label(caption);
        label.Anchor = AnchorStyles.Left;
        var row = WatchTheme.Actions(label, points);
        row.Anchor = AnchorStyles.Left;
        return row;
    }

    /// @brief 各画面の基本文字と、その画面の見出しに設定された相対サイズを保つ。
    private void ApplyDisplayTypography()
    {
        int points = Math.Clamp(__state.TextSizePoints, MinimumDisplayPoints, MaximumDisplayPoints);
        ApplyFormTypography(this, points);
        ApplyFormTypography(__management, points);
        if (__historyWindow is { } history)
        {
            ApplyFormTypography(history, points);
        }

        __trayMenu.Font = Font;
        UpdateOptionsScrollWidth();
        ResizeInboxMetrics();
        SizeDisplayList(__targetList, points);
        if (__historyRows is { } historyRows)
        {
            SizeDisplayList(historyRows, points);
        }
    }

    private void ApplyFormTypography(Form form, int points)
    {
        // 自動継承する通常ラベルと、専用サイズを持つ見出しを区別する。
        // 変更前に取得するので、サイズを何度切り替えても大きさが累積しない。
        Control[] controls = DisplayDescendants(form).ToArray();
        Label[] labels = controls.OfType<Label>().ToArray();
        foreach (var label in labels)
        {
            __displayLabels.GetValue(label, value =>
            {
                var font = value.Font;
                float offset = ReferenceEquals(font, value.Parent?.Font) ? 0 : font.SizeInPoints - DefaultDisplayPoints;
                return new DisplayLabelStyle(offset, font.Style);
            });
        }

        form.SuspendLayout();
        try
        {
            form.Font = GetDisplayFont(points, FontStyle.Regular);
            foreach (var strip in controls.OfType<ToolStrip>())
            {
                strip.Font = form.Font;
            }

            foreach (var label in labels)
            {
                var style = __displayLabels.GetValue(label, _ => new DisplayLabelStyle(0, FontStyle.Regular));
                label.Font = GetDisplayFont(points + style.Offset, style.Style);
            }
        }
        finally { form.ResumeLayout(performLayout: true); }
    }

    private static IEnumerable<Control> DisplayDescendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var descendant in DisplayDescendants(child))
            {
                yield return descendant;
            }
        }
    }

    private Font GetDisplayFont(float points, FontStyle style)
    {
        // コントロールごとに生成せず、同じサイズと書体はウィンドウの寿命中で共有する。
        var key = (points, style);
        if (!__displayFonts.TryGetValue(key, out var font))
        {
            font = new Font("Yu Gothic UI", points, style);
            __displayFonts.Add(key, font);
        }

        return font;
    }

    private void ResizeInboxMetrics()
    {
        if (__inboxColumns is null)
        {
            return;
        }

        // Font.Height は描画用 DPI を含まない場合があるため、同じ単位（px）にそろえる。
        int fontPixels = ScreenFontHeight(__inbox);
        __inbox.ItemHeight = Math.Max(ScaleForDisplay(__inbox, 21, scaleText: false), fontPixels + ScaleForDisplay(__inbox, 4, scaleText: false));
        __inbox.Indent = ScaleForDisplay(__inbox, 18);
        __inboxColumns.Height = fontPixels + ScaleForDisplay(__inbox, 8, scaleText: false);
        __selectedPath.Height = fontPixels * 2 + ScaleForDisplay(__selectedPath, 8, scaleText: false);
        __inbox.Invalidate();
        __inboxColumns.Invalidate();
    }

    private static int ScreenFontHeight(Control control) =>
        (int)Math.Ceiling(control.Font.GetHeight(control.DeviceDpi));

    /// @brief 文字量とモニター DPI の両方に合わせて設計寸法を補正する。
    private static int ScaleForDisplay(Control control, int pixels, bool scaleText = true) =>
        (int)Math.Round(pixels * control.DeviceDpi / 96f *
            (scaleText ? control.Font.SizeInPoints / DefaultDisplayPoints : 1));

    private void SizeDisplayList(ListView list, int points)
    {
        var previous = __displayLists.GetValue(list, _ => new DisplayListScale());
        if (list.Columns.Count == 0 || previous.Points == points)
        {
            return;
        }

        // 最終パス列は残り幅合わせに任せ、その他の列は設定前の比率を維持する。
        float ratio = (float)points / previous.Points;
        previous.Points = points;
        foreach (ColumnHeader column in list.Columns.Cast<ColumnHeader>().SkipLast(1))
        {
            column.Width = Math.Max(ScaleForDisplay(list, 36, scaleText: false), (int)Math.Round(column.Width * ratio));
        }
    }

    private void UpdateOptionsScrollWidth()
    {
        if (__settingsTabs.TabPages.Count < 2 ||
            __settingsTabs.TabPages[1].Controls[0] is not Panel scroll ||
            scroll.Controls[0] is not TableLayoutPanel options)
        {
            return;
        }

        // 幅の狭い設定画面と大きな文字の組み合わせでもチェックボックス等を切り捨てない。
        // 空き寸法を広げれば横スクロールは標準のパネルが非表示にする。
        int minWidth = options.Controls.OfType<SettingsGroup>()
            .Select(group => group.Controls[0].GetPreferredSize(Size.Empty).Width + group.Padding.Horizontal + group.Margin.Horizontal)
            .DefaultIfEmpty(0).Max() + scroll.Padding.Horizontal;
        scroll.AutoScrollMinSize = new Size(minWidth, 0);
    }

    /// @brief 通常の設定操作を通して文字サイズと狭い一覧の列境界を確認する。
    private void VerifyDisplayTypography()
    {
        int baseline = __state.TextSizePoints;
        int previousHeight = __inbox.ItemHeight;
        Size mainWindowSize = Size;
        Size settingsSize = __management.Size;
        int activeTab = __settingsTabs.SelectedIndex;
        var option = (NumericUpDown)__management.Controls.Find("displayTextSize", true).Single();
        try
        {
            Size = MinimumSize;
            __management.Size = __management.MinimumSize;
            __settingsTabs.SelectedIndex = 1;
            option.Value = MaximumDisplayPoints;
            PerformLayout();

            SelfTest.Check(Font.SizeInPoints == MaximumDisplayPoints &&
                __inbox.Font.SizeInPoints == MaximumDisplayPoints &&
                __targetList.Font.SizeInPoints == MaximumDisplayPoints &&
                __inbox.ItemHeight > previousHeight &&
                __store.Load().State.TextSizePoints == MaximumDisplayPoints,
                "display text size applies to both screens, resizes rows, and persists");

            var columns = __inbox.LayoutColumns;
            SelfTest.Check(columns.Change > 0 && columns.Change < columns.Updater &&
                columns.Updater < columns.Detected && columns.Detected < columns.Action &&
                columns.Action < columns.End, "large text keeps compact inbox columns ordered");

            var options = (Panel)__settingsTabs.TabPages[1].Controls[0];
            options.ScrollControlIntoView(option);
            SelfTest.Check(options.AutoScroll && options.ClientRectangle.Contains(
                options.RectangleToClient(option.RectangleToScreen(option.ClientRectangle))),
                "the text size input remains reachable at small window size");
        }
        finally
        {
            option.Value = baseline;
            __settingsTabs.SelectedIndex = activeTab;
            __management.Size = settingsSize;
            Size = mainWindowSize;
        }
    }

    private void DisposeDisplayFonts()
    {
        foreach (var font in __displayFonts.Values)
        {
            font.Dispose();
        }

        __displayFonts.Clear();
    }
}
