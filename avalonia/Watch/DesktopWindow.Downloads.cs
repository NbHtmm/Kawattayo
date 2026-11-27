using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace ShareFileWatch;

internal sealed partial class DesktopWindow
{
    private Button? __downloadButton;
    private CancellationTokenSource? __downloadStop;

    private static bool CanDownload(Row row) => row is { Folder: false, Path: not null }
        && row.Change?.Kind != "消失" && (row.Change is not null || row.Target?.LastChange != "消失");

    private void RefreshDownloadButton()
    {
        if (__downloadButton is not { } button) return;
        int count = Selected(__inbox).Count(CanDownload);
        button.IsEnabled = __downloadStop is not null || count > 0;
        button.Content = __downloadStop is not null ? "中止" : count > 1 ? $"ダウンロード ({count})…" : "ダウンロード…";
    }

    private async Task DownloadRows(Row[] selection, bool open)
    {
        if (__downloadStop is not null || __closing) return;
        var rows = selection.Where(CanDownload).DistinctBy(row => row.Path, StringComparer.OrdinalIgnoreCase).ToArray();
        if (rows.Length == 0 || (open && (rows.Length != 1 || !LocalDownloads.CanOpen(rows[0].Path!)))) return;

        using var stop = new CancellationTokenSource();
        __downloadStop = stop;
        RefreshDownloadButton();
        var acknowledge = open ? CaptureAcknowledgement(rows[0]) : null;
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new() { Title = "ローカルの保存先を選択" });
            if (folders.FirstOrDefault()?.TryGetLocalPath() is not { } folder || __closing || stop.IsCancellationRequested)
                return;

            var requests = rows.Select(row => new DownloadRequest(row.Path!,
                rows.Length > 1 && row.Change is { } change ? change.Relative : Leaf(row.Path!), SourceRoot: row.Target is { IsFolder: true } target ? target.Path : null)).ToArray();
            var result = await Task.Run(() => new LocalDownloads().Save(folder, requests, stop.Token));
            if (__closing || __closed) return;
            if (open && result[0].Saved is { } saved && !stop.IsCancellationRequested)
            {
                try
                {
                    if (!await Task.Run(() => WindowsShell.OpenFile(saved))) throw new IOException("保存済みファイルを開けません。");
                    if (!__closing && !__closed && __state.ReadOnFileOpen) { acknowledge!(); __monitor.UpdateSettings(); }
                    return;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or Win32Exception)
                {
                    if (!__closing && !__closed)
                        WinDesktop.Message($"保存しましたが、開けませんでした。\n{saved}\n{error.Message}", Handle);
                    return;
                }
            }
            await ShowDownloadResult(folder, result);
        }
        catch (Exception error)
        {
            if (!__closing && !__closed) WinDesktop.Message("ダウンロードできません：" + error.Message, Handle);
        }
        finally
        {
            __downloadStop = null;
            RefreshDownloadButton();
        }
    }

    private async Task ShowDownloadResult(string folder, DownloadResult[] result)
    {
        int saved = result.Count(x => x.Saved is not null);
        var dialog = new Window { Title = "ダウンロード結果", Width = 610, Height = 310,
            MinWidth = 360, MinHeight = 220, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var buttons = Bar(Button("閉じる", () => dialog.Close()));
        if (saved > 0) buttons.Children.Insert(0, Button("保存先を開く", () =>
        {
            try { WindowsShell.OpenFolder(folder); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or Win32Exception)
            { WinDesktop.Message("保存先を開けません：" + error.Message, dialog.TryGetPlatformHandle()?.Handle ?? 0); }
        }));
        var detail = new TextBox { IsReadOnly = true, AcceptsReturn = true,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Text = string.Join(Environment.NewLine, result.Select(x => x.Saved is { } path
                ? $"保存済み：{path}" : $"未保存：{x.Source} ｜ {x.Error}")) };
        dialog.Content = DockPage(new TextBlock { Text = $"保存 {saved:N0} 件 / 未保存 {result.Length - saved:N0} 件",
            Margin = new Thickness(0, 5) }, detail, buttons);
        await dialog.ShowDialog(this);
    }
}
