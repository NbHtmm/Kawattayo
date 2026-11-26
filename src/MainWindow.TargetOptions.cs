namespace ShareFileWatch;

internal sealed partial class MainWindow
{
    private const int TargetNameLimit = 120;
    private readonly CheckBox __renotifyUnread = new() { Text = "未読のファイルも更新のたびに通知する", AutoSize = true };

    /// @brief 空欄の場合は実際のファイル・フォルダ名を表示する。
    private static string TargetDisplayName(WatchItem item) =>
        string.IsNullOrWhiteSpace(item.DisplayName) ? DisplayName(item.Path) : item.DisplayName.Trim();

    private Control BuildExclusionOptions()
    {
        var patterns = new TextBox
        {
            Multiline = true, ScrollBars = ScrollBars.Vertical, AcceptsReturn = true,
            Dock = DockStyle.Fill, MinimumSize = new Size(0, 85), MaxLength = 8192,
            AccessibleName = "除外ファイル名のパターン", Text = string.Join(Environment.NewLine, __state.ExcludedFilePatterns)
        };
        var help = WatchTheme.Label("1行に1つ。ファイル名のみ指定できます（例：~$*、*.tmp）。", 10);
        var office = WatchTheme.Button("Officeの一時ファイルを追加", () =>
        {
            if (!patterns.Lines.Any(line => line.Trim().Equals("~$*", StringComparison.Ordinal)))
            {
                patterns.Text = string.IsNullOrWhiteSpace(patterns.Text)
                    ? "~$*"
                    : patterns.Text.TrimEnd() + Environment.NewLine + "~$*";
            }
            patterns.Focus();
        });
        var apply = WatchTheme.Button("除外ルールを適用", () => ApplyExclusionOptions(patterns), primary: true);
        var group = WatchTheme.Layout(rows: 3);
        group.AutoSize = true;
        group.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        group.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        group.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        group.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        group.Controls.Add(help, 0, 0);
        group.Controls.Add(patterns, 0, 1);
        group.Controls.Add(WatchTheme.Actions(office, apply), 0, 2);
        return group;
    }

    private void ApplyExclusionOptions(TextBox input)
    {
        try
        {
            var patterns = FileExclusions.Parse(input.Text);
            SetExcludedFilePatterns(patterns);
            input.Text = string.Join(Environment.NewLine, __state.ExcludedFilePatterns);
            Save();
            RefreshRows();
            StartTimer(immediate: true);
        }
        catch (ArgumentException ex)
        {
            MessageBox.Show(__management, ex.Message, AppDefaults.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            input.Focus();
        }
    }

    /// @brief 名前変更は表示だけに反映し、監視対象のパスや実ファイルは変更しない。
    private void RenameTarget(WatchItem item)
    {
        using var dialog = new Form
        {
            Text = "表示名を変更", Font = __management.Font, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, FormBorderStyle = FormBorderStyle.FixedDialog,
            ShowInTaskbar = false, MinimizeBox = false, MaximizeBox = false, StartPosition = FormStartPosition.CenterParent
        };
        var input = new TextBox
        {
            Text = item.DisplayName ?? "", MaxLength = TargetNameLimit,
            PlaceholderText = DisplayName(item.Path), AccessibleName = "表示名", Dock = DockStyle.Fill
        };
        var accept = WatchTheme.Button("保存", () => { }, primary: true);
        accept.DialogResult = DialogResult.OK;
        var cancel = WatchTheme.Button("キャンセル", () => { });
        cancel.DialogResult = DialogResult.Cancel;
        var layout = WatchTheme.Layout(rows: 3);
        layout.Dock = DockStyle.Fill;
        layout.AutoSize = true;
        layout.Padding = new Padding(16);
        layout.MinimumSize = new Size(Math.Max(360, TextRenderer.MeasureText("空欄にすると元のファイル・フォルダ名を表示します。", dialog.Font).Width + 42), 0);
        layout.Controls.Add(new Label { Text = "空欄にすると元のファイル・フォルダ名を表示します。", AutoSize = true }, 0, 0);
        layout.Controls.Add(input, 0, 1);
        layout.Controls.Add(WatchTheme.Actions(accept, cancel), 0, 2);
        dialog.AcceptButton = accept;
        dialog.CancelButton = cancel;
        dialog.Controls.Add(layout);
        dialog.Shown += (_, _) => { input.Focus(); input.SelectAll(); };
        if (dialog.ShowDialog(__management) == DialogResult.OK && __state.Items.Contains(item))
        {
            SetTargetDisplayName(item, input.Text);
        }
    }

    private void SetTargetDisplayName(WatchItem item, string name)
    {
        var trimmed = name.Trim();
        item.DisplayName = trimmed.Length <= TargetNameLimit ? trimmed : trimmed[..TargetNameLimit];
        Save();
        RefreshRows();
    }

    private ContextMenuStrip BuildTargetMenu(WatchItem item)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("開く", null, async (_, _) => await OpenRegisteredTarget(item, parent: false));
        if (!item.IsFolder)
        {
            menu.Items.Add("親フォルダを開く", null, async (_, _) => await OpenRegisteredTarget(item, parent: true));
        }

        menu.Items.Add("表示名を変更…", null, (_, _) => RenameTarget(item));
        menu.Items.Add("コピー", null, async (_, _) => await CopyInboxTarget(item.Path));
        menu.Items.Add("パスをコピー", null, (_, _) => CopyText(item.Path));
        AddRecursiveCommands(menu, [item]);
        var captured = CaptureWholeTarget(item);
        menu.Items.Add("確認済みにする", null, (_, _) => ConfirmCapturedTarget(item, captured)).Enabled = item.Unread > 0;
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("登録解除", null, (_, _) => Unregister(item));
        return menu;
    }

    /// @brief 設定一覧からでも、開く前に表示されていた更新だけを既読にする。
    private async Task OpenRegisteredTarget(WatchItem item, bool parent)
    {
        var path = parent ? Path.GetDirectoryName(item.Path) : item.Path;
        if (path is null || !__state.Items.Contains(item))
        {
            return;
        }

        var acknowledge = CaptureWholeTarget(item);
        await OpenAndConfirm(item, path, acknowledge, item.IsFolder || parent ? null : __verifyFileOpen ?? WindowsShell.OpenFile);
    }

    private static Action CaptureWholeTarget(WatchItem item) => GetAcknowledgement(item,
        new InboxRow(item.Id, TargetDisplayName(item), null, null, false, item.LastChanged, item.LastModified, item.LastChange, item.IsFolder, item.Recursive));

    private void ConfirmCapturedTarget(WatchItem item, Action acknowledge)
    {
        // メニューが開いたまま登録が消えた場合は何もしない。
        if (!__state.Items.Contains(item))
        {
            return;
        }

        acknowledge();
        Save();
        RefreshRows();
    }
}
