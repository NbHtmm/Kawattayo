namespace ShareFileWatch;

internal sealed partial class MainWindow
{
    private void VerifyDisplayTypography()
    {
        int baseline = __state.TextSizePoints;
        int previousHeight = __inbox.ItemHeight;
        int previousButton = __scanNow.Width;
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
                __scanNow.Width > previousButton && !__viewport.HorizontalScroll.Visible &&
                __store.Load().State.TextSizePoints == MaximumDisplayPoints,
                "display text size applies to both screens, resizes rows, and persists");

            var columns = __inbox.LayoutColumns;
            SelfTest.Check(columns.Change > 0 && columns.Change >= __inbox.ClientSize.Width * 0.49 && columns.Change < columns.Updater &&
                columns.Updater <= columns.Detected && columns.Detected < columns.Action &&
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
}
