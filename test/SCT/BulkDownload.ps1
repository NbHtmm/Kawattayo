# Exercise actual multi-selection, the native folder picker, and the packaged result dialog.
function Test-SctBulkDownload($Process,$Monitor,[string]$Work,[string[]]$Sources,[string]$Evidence) {
    $destination = Join-Path $Work 'saved'
    $child = Join-Path $destination 'nested'
    [void][IO.Directory]::CreateDirectory($child)
    $sentinel = Join-Path $child 'SCT-Alpha.txt'
    $saved = Join-Path $child 'SCT-Beta.txt'
    [IO.File]::WriteAllText($sentinel,'Do not overwrite this preexisting file.')
    $sentinelHash = (Get-FileHash -LiteralPath $sentinel).Hash
    $hashes = @($Sources | ForEach-Object { (Get-FileHash -LiteralPath $_).Hash })

    $first = Get-SctDownloadSelection $Monitor 'SCT-Alpha.txt'
    $second = Get-SctDownloadSelection $Monitor 'SCT-Beta.txt'
    $first.Select()
    $second.AddToSelection()
    $button = 'ダウンロード (2)…'
    [void](Wait-SctUi 'two file rows selected for download' { Get-SctUiLabel $Monitor $button })
    Invoke-SctUiButton $Monitor $button

    $picker = Wait-SctUi 'native folder picker' {
        Get-SctOwnedWindow $Process.Id 'ローカルの保存先を選択'
    }
    Submit-SctNativePicker $picker $Process.Id $destination
    # The shell may first navigate to a typed folder instead of returning it.
    # Only confirm a second time after the address bar proves we are inside the fixture.
    $timer = [Diagnostics.Stopwatch]::StartNew()
    while ($timer.Elapsed.TotalSeconds -lt 4) {
        if (Get-SctOwnedWindow $Process.Id 'ダウンロード結果') { break }
        $current = Get-SctOwnedWindow $Process.Id 'ローカルの保存先を選択'
        if ($current) {
            $condition = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,'1001')
            $address = $current.FindFirst([Windows.Automation.TreeScope]::Descendants,$condition)
            if ($address -and $address.Current.Name.EndsWith($destination,[StringComparison]::OrdinalIgnoreCase)) {
                Submit-SctNativePicker $current $Process.Id $destination
                break
            }
        }
        Start-Sleep -Milliseconds 150
    }

    try { $result = Wait-SctUi 'bulk download reports one saved and one not saved' {
        $window = Get-SctOwnedWindow $Process.Id 'ダウンロード結果'
        if ($window -and (Get-SctUiLabel $window '保存 1 件 / 未保存 1 件')) { $window }
    } } catch {
        foreach ($title in 'ローカルの保存先を選択','ダウンロード結果','かわったよ') {
            $dialog = Get-SctOwnedWindow $Process.Id $title
            if (!$dialog) { continue }
            $rows = @($dialog.FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.Condition]::TrueCondition) |
                ForEach-Object { $_.Current.Name } | Where-Object { $_ })
            [IO.File]::AppendAllLines($Evidence,[string[]](@($title)+$rows))
        }
        throw
    }

    if ((Get-FileHash -LiteralPath $sentinel).Hash -ne $sentinelHash -or
        !(Test-Path -LiteralPath $saved -PathType Leaf) -or
        (Get-FileHash -LiteralPath $saved).Hash -ne $hashes[1]) {
        throw 'Bulk download changed the existing file or did not produce the correct second output.'
    }
    for ($index = 0; $index -lt $Sources.Count; $index++) {
        if ((Get-FileHash -LiteralPath $Sources[$index]).Hash -ne $hashes[$index]) {
            throw "Bulk download modified source $($Sources[$index])."
        }
    }
    Assert-SctDownloadDetails $result $Sources[0] $saved
    Invoke-SctUiButton $result '閉じる'
    [IO.File]::AppendAllText($Evidence,"PASS one collision rejected, one relative-path copy saved, both sources and existing destination unchanged." + [Environment]::NewLine)
}

function Get-SctDownloadSelection($Window,[string]$Label) {
    $element = Get-SctUiLabel $Window $Label
    $walker = [Windows.Automation.TreeWalker]::RawViewWalker
    while ($element -and $element.Current.ProcessId -eq $Window.Current.ProcessId) {
        $pattern = $null
        if ($element.TryGetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern,[ref]$pattern)) {
            return $pattern
        }
        $element = $walker.GetParent($element)
    }
    throw "SelectionItem pattern not available for $Label."
}

function Submit-SctNativePicker($Picker,[int]$ProcessId,[string]$Path,[switch]$FilePicker) {
    if (!('SctFolderPickerNative' -as [type])) {
        Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class SctFolderPickerNative
{
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out int pid);
    [DllImport("user32.dll")] private static extern IntPtr GetDlgItem(IntPtr h, int id);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, StringBuilder text, int size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessageTimeout(
        IntPtr h, uint message, IntPtr wparam, string value, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr h, uint message, IntPtr wparam, IntPtr lparam);
    private static bool Validate(IntPtr h, int expected, string type)
    {
        var name = new StringBuilder(64);
        int pid;
        GetWindowThreadProcessId(h, out pid);
        GetClassName(h, name, name.Capacity);
        return pid == expected && name.ToString().Equals(type, StringComparison.OrdinalIgnoreCase);
    }
    public static string Submit(IntPtr dialog, int process, string path, bool filePicker)
    {
        if (!Validate(dialog, process, "#32770")) return "unexpected native picker";
        IntPtr submit = GetDlgItem(dialog, 1);
        IntPtr edit;
        if (filePicker)
        {
            // The file name field is nested: combo-box extension, combo box, then edit.
            var outer = GetDlgItem(dialog, 1148);
            if (!Validate(outer, process, "ComboBoxEx32")) return "missing file name control";
            var combo = GetDlgItem(outer, 1148);
            if (!Validate(combo, process, "ComboBox")) return "unexpected file name control";
            edit = GetDlgItem(combo, 1148);
        }
        else edit = GetDlgItem(dialog, 1152);
        if (!Validate(edit, process, "Edit") || !Validate(submit, process, "Button")) return "unexpected picker children";
        // The native picker does not always expose Value/Invoke providers to UI Automation.
        IntPtr result;
        if (SendMessageTimeout(edit, 0x000C, IntPtr.Zero, path, 2, 3000, out result) == IntPtr.Zero || result == IntPtr.Zero)
            return "picker field rejected the path";
        return PostMessage(submit, 0x00F5, IntPtr.Zero, IntPtr.Zero) ? null : "picker confirmation failed";
    }
}
"@
    }
    $errorText = [SctFolderPickerNative]::Submit([IntPtr]$Picker.Current.NativeWindowHandle,$ProcessId,$Path,$FilePicker.IsPresent)
    if ($errorText) { throw "Could not submit the isolated fixture path: $errorText." }
}

function Assert-SctDownloadDetails($Dialog,[string]$Rejected,[string]$Saved) {
    $scope = [Windows.Automation.TreeScope]::Descendants
    foreach ($element in $Dialog.FindAll($scope,[Windows.Automation.Condition]::TrueCondition)) {
        $pattern = $null
        if ($element.TryGetCurrentPattern([Windows.Automation.ValuePattern]::Pattern,[ref]$pattern)) {
            $value = $pattern.Current.Value
            if ($value.Contains("未保存：$Rejected") -and $value.Contains("保存済み：$Saved")) { return }
        }
    }
    throw 'Result dialog did not identify both per-file outcomes.'
}
