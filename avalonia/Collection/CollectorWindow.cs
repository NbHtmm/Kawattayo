using Avalonia.Controls.Templates;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;

namespace ShareFileWatch;

internal sealed class CollectorWindow : Window
{
    private readonly Checkpoints checkpoints;
    private readonly ComboBox saved = new() { MinWidth = 300, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox name = new() { PlaceholderText = "例：試験前" };
    private readonly TextBox root = new() { PlaceholderText = @"ローカルフォルダまたは \\server\share" };
    private readonly TextBox exclusions = new() { PlaceholderText = "1行に1パターン（例：~$*）", AcceptsReturn = true, Height = 52 };
    private readonly CheckBox recursive = new() { IsChecked = true, Content = "配下のフォルダも比較する" };
    private readonly ListBox results = new() { SelectionMode = SelectionMode.Multiple };
    private readonly TextBlock status = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly Button create, compare, collect, reload, cancel;
    private CancellationTokenSource? work;
    private CheckpointInfo? preview;

    internal CollectorWindow(Checkpoints checkpoints, Action showMonitor)
    {
        this.checkpoints = checkpoints;
        // Placeholders disappear after input; keep the fields named for assistive technology.
        Avalonia.Automation.AutomationProperties.SetName(name, "チェックポイント名");
        Avalonia.Automation.AutomationProperties.SetName(root, "比較対象フォルダ");
        Avalonia.Automation.AutomationProperties.SetName(exclusions, "除外パターン");
        saved.ItemTemplate = new FuncDataTemplate<CheckpointInfo>((item, _) => item is null ? null : new TextBlock { Text = CheckpointLabels.Describe(item) });
        results.ItemTemplate = new FuncDataTemplate<CheckpointDifference>((item, _) => item is null ? null : new TextBlock { Text = CheckpointLabels.Describe(item) });
        Title = "かわったよ · 差分収集";
        using (var icon = GetType().Assembly.GetManifestResourceStream("collector.ico"))
            if (icon is not null) Icon = new WindowIcon(icon);
        Width = 820; Height = 630; MinWidth = 480; MinHeight = 460;
        create = Command("チェックポイントを作成", Create);
        compare = Command("比較", Compare);
        collect = Command("選択分をダウンロード…", Collect);
        reload = Command("一覧を再読込", async () => await Run(stop => Reload(stop)));
        cancel = Command("中止", () => { work?.Cancel(); return Task.CompletedTask; });
        var browse = Command("参照…", async () =>
        {
            string? folder = await PickFolder("比較するフォルダ");
            if (folder is not null) root.Text = folder;
        });
        var fields = new StackPanel { Spacing = 5, Children =
        {
            new TextBlock { Text = "1. チェックポイントを作る", FontWeight = Avalonia.Media.FontWeight.SemiBold },
            name, new DockPanel { LastChildFill = true, Children = { Right(browse), root } },
            recursive, exclusions, create,
            new TextBlock { Text = "2. チェックポイントと比較する", Margin = new Thickness(0, 7, 0, 0), FontWeight = Avalonia.Media.FontWeight.SemiBold },
            new DockPanel { LastChildFill = true, Children = { Right(compare), Right(reload), saved } },
            new TextBlock { Text = "3. 一覧からダウンロードする項目を選ぶ", Margin = new Thickness(0, 7, 0, 0), FontWeight = Avalonia.Media.FontWeight.SemiBold }
        }};
        var bottom = new StackPanel { Spacing = 6, Children =
        {
            new WrapPanel { Children = { collect, cancel, Command("更新監視を開く", () => { showMonitor(); return Task.CompletedTask; }) } },
            new ScrollViewer { MaxHeight = 92, Content = status }
        }};
        DockPanel.SetDock(fields, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        Content = new DockPanel { Margin = new Thickness(14), LastChildFill = true, Children = { fields, bottom, results } };
        Opened += async (_, _) => await Run(stop => Reload(stop));
        Closing += (_, _) => work?.Cancel();
        saved.SelectionChanged += (_, _) => { ClearPreview(); UpdateButtons(); };
        results.SelectionChanged += (_, _) => UpdateButtons();
        UpdateButtons();
    }

    private static Control Right(Control control)
    {
        DockPanel.SetDock(control, Dock.Right);
        return control;
    }

    private static Button Command(string text, Func<Task> action)
    {
        var button = new Button { Content = text, Margin = new Thickness(3, 0, 3, 0) };
        button.Click += async (_, _) => await action();
        return button;
    }

    private Task Create() => Run(async stop =>
    {
        string label = name.Text ?? "", path = root.Text ?? "", filters = exclusions.Text ?? "";
        bool nested = recursive.IsChecked == true;
        var value = await Task.Run(() => checkpoints.Create(label, path, nested, filters, stop), stop);
        await Reload(stop, value.Id);
    });

    private Task Compare() => Run(async stop =>
    {
        if (saved.SelectedItem is not CheckpointInfo item) return;
        ClearPreview();
        var result = await Task.Run(() => checkpoints.Compare(item.Id, stop), stop);
        if ((saved.SelectedItem as CheckpointInfo)?.Id != item.Id) return;
        preview = result.Info;
        results.ItemsSource = result.Changes;
        int unconfirmed = result.Changes.Count(x => x.Kind == DifferenceKind.Unconfirmed);
        status.Text = $"差分・確認不能：{result.Changes.Length:N0}件（確認不能 {unconfirmed:N0}件）";
    });

    private Task Collect() => Run(async stop =>
    {
        if (preview is not { } info) return;
        var selected = SelectedChanges();
        var output = await CollectorExportSelection.ResolveDestination(info, selected.Length,
            () => preview, () => PickFolder("保存先を選択"), stop);
        if (output is null) return;
        var copies = await Task.Run(() => checkpoints.Collect(info, selected, output, stop), stop);
        status.Text = $"保存 {copies.Count(x => x.Saved is not null):N0}件 ／ 未保存 {copies.Count(x => x.Saved is null):N0}件\n" +
            string.Join("\n", copies.Select(x => x.Saved ?? $"{x.Source}：{x.Error}"));
    });

    private CheckpointDifference[] SelectedChanges() =>
        results.SelectedItems?.OfType<CheckpointDifference>().Where(x => x.CanDownload).ToArray() ?? [];

    private async Task Reload(CancellationToken stop, string? select = null)
    {
        select ??= (saved.SelectedItem as CheckpointInfo)?.Id;
        var values = await Task.Run(checkpoints.List, stop);
        saved.ItemsSource = values;
        saved.SelectedItem = values.FirstOrDefault(x => x.Id == select) ?? values.FirstOrDefault();
        status.Text = $"チェックポイント：{values.Length:N0}件";
    }

    private void ClearPreview()
    {
        preview = null;
        results.ItemsSource = null;
    }

    private async Task Run(Func<CancellationToken, Task> action)
    {
        if (work is not null) return;
        using var source = new CancellationTokenSource();
        work = source;
        status.Text = "処理中…";
        UpdateButtons();
        try { await action(source.Token); }
        catch (OperationCanceledException) { status.Text = "中止しました。"; }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { status.Text = ex.Message; }
        finally { work = null; UpdateButtons(); }
    }

    private void UpdateButtons()
    {
        bool idle = work is null;
        create.IsEnabled = reload.IsEnabled = saved.IsEnabled = idle;
        compare.IsEnabled = idle && saved.SelectedItem is CheckpointInfo;
        collect.IsEnabled = idle && preview is not null && SelectedChanges().Length != 0;
        cancel.IsEnabled = !idle;
    }

    private async Task<string?> PickFolder(string title)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new() { Title = title, AllowMultiple = false });
        return folders.FirstOrDefault()?.TryGetLocalPath();
    }
}
