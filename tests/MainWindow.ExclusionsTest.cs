namespace ShareFileWatch;

/// @brief 除外ルールを設定画面から適用し、新旧の通知と監視基準を検証する。
internal sealed partial class MainWindow
{
    private async Task VerifyExclusionSettings(string root)
    {
        var folder = Directory.CreateDirectory(Path.Combine(root, "gui-exclusions")).FullName;
        SetExcludedFilePatterns([]); // exercise opting out, then re-applying the Office preset
        var temp = Path.Combine(folder, "~$GuiDraft.docx");
        var ordinary = Path.Combine(folder, "GuiDraft.docx");
        await File.WriteAllTextAsync(temp, "office lock");
        await File.WriteAllTextAsync(ordinary, "normal file");
        SelfTest.Check(await RegisterPaths([folder, temp]), "register folder and explicitly watched Office file");
        await Scan();

        await File.AppendAllTextAsync(temp, " first edit");
        await Scan();
        var folderTarget = __state.Items.Single(x => x.Path == folder);
        var fileTarget = __state.Items.Single(x => x.Path == temp);
        SelfTest.Check(folderTarget.Unread > 0 && fileTarget.Unread > 0, "Office file has existing unread events before filtering");

        ShowManagement();
        __settingsTabs.SelectedIndex = 1;
        var displayOptions = DisplayDescendants(__settingsTabs.TabPages[1]).ToArray();
        var text = displayOptions.OfType<TextBox>().Single(x => x.AccessibleName == "除外ファイル名のパターン");
        var apply = displayOptions.OfType<Button>().Single(x => x.Text == "除外ルールを適用");
        var preset = displayOptions.OfType<Button>().Single(x => x.Text == "Officeの一時ファイルを追加");
        preset.PerformClick();
        SelfTest.Check(FileExclusions.Matches(temp, FileExclusions.Parse(text.Text)),
            "Office preset puts an exclusion into the settings editor");
        apply.PerformClick();
        SelfTest.Check(FileExclusions.Matches(temp, __store.Load().State.ExcludedFilePatterns) &&
            fileTarget.Unread == 0 && folderTarget.Unread == 0, "apply persists exclusion and clears excluded unread items");
        var targetRow = __targetList.Items.Cast<ListViewItem>().Single(row => Equals(row.Tag, fileTarget.Id));
        SelfTest.Check(targetRow.SubItems[1].Text == "0" && targetRow.SubItems[2].Text == "除外中", "excluded direct registration stays listed with its status");
        var historyCount = __state.History.Count;
        await File.AppendAllTextAsync(temp, " silent edit");
        await File.AppendAllTextAsync(ordinary, " ordinary edit");
        await Scan();
        SelfTest.Check(fileTarget.LastChecked is null && fileTarget.Unread == 0 &&
            folderTarget.PendingFiles.Keys.SequenceEqual([Path.GetFileName(ordinary)]) &&
            __state.History.Count == historyCount + 1, "ignored files are not probed or notified, normal updates survive");

        text.Clear();
        apply.PerformClick();
        historyCount = __state.History.Count;
        await Scan();
        SelfTest.Check(fileTarget.Baseline == ProbeState.Found && __state.History.Count == historyCount,
            "re-enabling excluded files does not produce false additions");
        __settingsTabs.SelectedIndex = 0;
    }

}
