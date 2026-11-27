using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;

namespace ShareFileWatch;

internal sealed partial class DesktopWindow
{
    private async Task ConfigureHook(WatchItem target)
    {
        var rule = __monitor.Hooks.Get(target.Id, target.Path);
        var use = new CheckBox { Content = "追加・更新時に実行する", IsChecked = rule is not null };
        var exe = new TextBox { Text = rule?.Executable, PlaceholderText = @"C:\Tools\Analyze.exe" };
        var args = new TextBox { Text = rule is null ? "" : string.Join(Environment.NewLine, rule.Arguments),
            AcceptsReturn = true, Height = 72, PlaceholderText = "固定引数を1行に1個（省略可）" };
        var stable = new NumericUpDown { Minimum = 0, Maximum = 300, Value = rule?.WaitSeconds ?? 15, Increment = 5 };
        var status = new TextBlock { Text = __monitor.Hooks.Status(target.Id), TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var dialog = new Window { Title = "更新フック ｜ " + Label(target), Width = 550, Height = 440,
            MinWidth = 460, MinHeight = 390, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var browse = Button("参照…", () => _ = BrowseHook(dialog, exe));
        var save = Button("保存", () =>
        {
            if (!__state.Items.Contains(target)) { dialog.Close(); return; }
            try
            {
                var value = use.IsChecked == true ? new HookRule(target.Path, exe.Text?.Trim() ?? "",
                    (args.Text ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries), (int)(stable.Value ?? 0)) : null;
                __monitor.Hooks.Set(target.Id, value);
                dialog.Close();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
            { status.Text = error.Message; }
        });
        dialog.Content = new ScrollViewer { Content = new StackPanel { Margin = new Thickness(14), Spacing = 6, Children =
        {
            use,
            new TextBlock { Text = "実行ファイル（現在のWindowsユーザーで起動します）" },
            exe, Bar(browse), args,
            Bar(new TextBlock { Text = "安定待ち（秒、0で待たない）", VerticalAlignment = VerticalAlignment.Center }, stable),
            new TextBlock { Text = "変更ファイルの絶対パスを最後の引数に渡します。監視先へ書き戻す処理は再検知の原因になります。",
                TextWrapping = Avalonia.Media.TextWrapping.Wrap },
            status, Bar(save, Button("キャンセル", () => dialog.Close()))
        }}};
        await dialog.ShowDialog(__settingsWindow?.IsActive == true ? __settingsWindow : this);
    }

    private static async Task BrowseHook(Window dialog, TextBox exe)
    {
        var files = await dialog.StorageProvider.OpenFilePickerAsync(new() { Title = "フック実行ファイル", FileTypeFilter =
            [new FilePickerFileType("Windows実行ファイル") { Patterns = ["*.exe"] }] });
        if (files.FirstOrDefault()?.TryGetLocalPath() is { } path) exe.Text = path;
    }
}
