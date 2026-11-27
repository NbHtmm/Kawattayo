using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using System.ComponentModel;

namespace ShareFileWatch;

internal sealed partial class DesktopWindow
{
    private async Task Pick(bool folders)
    {
        try
        {
            var owner = __settingsWindow?.IsVisible == true ? __settingsWindow : this;
            IEnumerable<IStorageItem> picked = folders
                ? await owner.StorageProvider.OpenFolderPickerAsync(new() { Title = "監視するフォルダを選択", AllowMultiple = true })
                : await owner.StorageProvider.OpenFilePickerAsync(new() { Title = "監視するファイルを選択", AllowMultiple = true });
            await Register(picked.Select(item => item.TryGetLocalPath()).OfType<string>());
        }
        catch (Exception error) { WinDesktop.Message("選択画面を開けません：" + error.Message, Handle); }
    }

    private async Task Register(IEnumerable<string> paths)
    {
        var requested = paths.ToArray();
        var results = await Task.Run(() => requested.Select(CheckRegistration).ToArray());
        if (__closing) return;
        var known = __state.Items.Select(x => x.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool added = false;
        foreach (var (path, folder, error) in results)
        {
            if (error is not null || !known.Add(path)) continue;
            __state.Items.Add(new() { Path = path, IsFolder = folder, Recursive = folder && __state.RecursiveFolders != false });
            added = true;
        }
        if (added) __monitor.UpdateSettings(true);
        var errors = results.Where(x => x.Error is not null).Take(5).Select(x => x.Path + "：" + x.Error).ToArray();
        if (errors.Length > 0) WinDesktop.Message(string.Join(Environment.NewLine, errors), Handle);
    }

    private (string Path, bool Folder, string? Error) CheckRegistration(string input)
    {
        try
        {
            string path = FileProbe.NormalizeFolder(input);
            if (__monitor.Store.IsInternalFile(path)) return (input, false, "自身の設定・キャッシュは監視できません");
            if (Directory.Exists(path)) return (path, true, null);
            if (File.Exists(path)) return (FileProbe.Normalize(path), false, null);
            return (input, false, "ファイルやフォルダが見つかりません");
        }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        { return (input, false, error.Message); }
    }

    private async Task Rename(WatchItem item)
    {
        var input = new TextBox { Text = item.DisplayName, PlaceholderText = Leaf(item.Path), MaxLength = 120 };
        var dialog = new Window { Title = "表示名を変更", Width = 400, Height = 150, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var controls = new StackPanel { Margin = new Thickness(14), Spacing = 6 };
        controls.Children.Add(input);
        controls.Children.Add(Bar(Button("保存", () => dialog.Close(true)), Button("キャンセル", () => dialog.Close(false))));
        dialog.Content = controls;
        dialog.Opened += (_, _) => input.Focus();
        input.KeyDown += (_, e) => { if (e.Key == Key.Enter) dialog.Close(true); };
        if (await dialog.ShowDialog<bool>(__settingsWindow?.IsActive == true ? __settingsWindow : this) && __state.Items.Contains(item))
        {
            item.DisplayName = input.Text?.Trim() ?? "";
            __monitor.UpdateSettings();
        }
    }

    private void SetRecursive(WatchItem item, bool? overrideValue)
    {
        item.RecursiveOverride = overrideValue;
        item.SetRecursive(overrideValue ?? (__state.RecursiveFolders != false));
        __monitor.UpdateSettings(true);
    }

    private void Remove(IEnumerable<WatchItem> items)
    {
        var targets = items.DistinctBy(x => x.Id).ToArray();
        if (targets.Length == 0 || !WinDesktop.Confirm($"{targets.Length}件の監視登録を解除しますか？ 実ファイルは削除しません。", Handle)) return;
        foreach (var target in targets)
        {
            __monitor.Hooks.Disable(target.Id);
            __state.Items.Remove(target);
        }
        __monitor.UpdateSettings(true);
    }

    private void SetExclusions(string text)
    {
        try
        {
            var rules = FileExclusions.Parse(text);
            __state.ExcludedFilePatterns = rules;
            foreach (var item in __state.Items)
            {
                if (!item.IsFolder && FileExclusions.Matches(item.Path, rules)) item.Acknowledge();
                if (!item.IsFolder) continue;
                foreach (var change in item.PendingFiles.Values.Where(x => x.Relative != "." &&
                    FileExclusions.Matches(x.Relative, rules)).ToArray())
                    item.AcknowledgeFile(change);
            }
            __monitor.UpdateSettings(true);
        }
        catch (ArgumentException error) { WinDesktop.Message(error.Message, Handle); }
    }

    // 確認開始後に差し替わった未読は残す。ファイルを開く待ち時間中にも照会は続く。
    private static Action CaptureAcknowledgement(Row row)
    {
        if (row.Target is not { } target) return () => { };
        if (row.Change is { } change) return () => target.AcknowledgeFile(change);
        var last = target.LastChanged;
        var unread = target.Unread;
        var entries = target.PendingFiles.Values.Where(change =>
            row.Root || (row.Path is { } path && Path.Combine(target.Path, change.Relative)
                .StartsWith(Path.TrimEndingDirectorySeparator(path) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))).ToArray();
        return () =>
        {
            if (row.Root && target.LastChanged == last && target.Unread == unread) { target.Acknowledge(); return; }
            foreach (var file in entries) target.AcknowledgeFile(file);
        };
    }

    private void Acknowledge(IEnumerable<Row> rows)
    {
        foreach (var row in rows.ToArray()) CaptureAcknowledgement(row)();
        __monitor.UpdateSettings();
    }

    private void ClearDeleted()
    {
        foreach (var item in __state.Items)
        {
            if (!item.IsFolder && item.LastChange == "消失") item.Acknowledge();
            foreach (var change in item.PendingFiles.Values.Where(x => x.Kind == "消失").ToArray()) item.AcknowledgeFile(change);
        }
        __monitor.UpdateSettings();
    }

    private async Task OpenRow(Row row, bool parent)
    {
        if (row.Path is not { } path) return;
        var acknowledge = CaptureAcknowledgement(row);
        try
        {
            bool opened = await Task.Run(() => parent
                ? Path.GetDirectoryName(path) is { } directory && WindowsShell.OpenFolder(directory)
                : row.Folder ? WindowsShell.OpenFolder(path) : WindowsShell.OpenFile(path));
            if (__closing) return;
            if (!opened) { WinDesktop.Message("対象を開けません：" + path, Handle); return; }
            if (__state.ReadOnFileOpen) { acknowledge(); __monitor.UpdateSettings(); }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException)
        { if (!__closing) WinDesktop.Message("対象を開けません：" + error.Message, Handle); }
    }

    private async Task Copy(Row[] rows, bool pathsOnly)
    {
        var paths = rows.Select(x => x.Path).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (paths.Length == 0 || Clipboard is not { } clipboard) return;
        try
        {
            if (pathsOnly) { await clipboard.SetTextAsync(string.Join(Environment.NewLine, paths)); return; }
            var transfer = new DataTransfer();
            int count = 0;
            foreach (var path in paths)
            {
                IStorageItem? item = await StorageProvider.TryGetFileFromPathAsync(path);
                item ??= await StorageProvider.TryGetFolderFromPathAsync(path);
                if (item is null) continue;
                transfer.Add(DataTransferItem.Create(DataFormat.File, item));
                count++;
            }
            if (count > 0) await clipboard.SetDataAsync(transfer);
            else WinDesktop.Message("コピーできるファイル・フォルダがありません。", Handle);
        }
        catch (Exception error) { if (!__closing) WinDesktop.Message("コピーできません：" + error.Message, Handle); }
    }

    private async void MainKeyboard(object? sender, KeyEventArgs e)
    {
        if ((e.KeyModifiers & KeyModifiers.Control) == 0 || e.Key is not (Key.R or Key.OemComma)) return;
        e.Handled = true;
        if (e.Key == Key.R) await __monitor.Scan();
        else ShowSettings();
    }

    private async void Keyboard(object? sender, KeyEventArgs e)
    {
        if (sender is not ListBox list || e.Source is TextBox or Avalonia.Controls.Button) return;
        bool ctrl = (e.KeyModifiers & KeyModifiers.Control) != 0;
        var rows = Selected(list);
        if (rows.Length == 0) return;
        if (e.Key == Key.Enter) { e.Handled = true; await OpenRow(rows[0], false); }
        else if (e.Key == Key.Space) { e.Handled = true; Acknowledge(rows); }
        else if (ctrl && e.Key == Key.C)
        {
            e.Handled = true;
            await Copy(rows, pathsOnly: list != __inbox || (e.KeyModifiers & KeyModifiers.Shift) != 0);
        }
        else if (e.Key == Key.F2 && rows[0].Root && rows[0].Target is { } target) { e.Handled = true; await Rename(target); }
    }
}
