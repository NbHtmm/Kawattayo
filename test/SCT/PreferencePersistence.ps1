# Drive only the packaged, isolated monitor. Use UIA for values; right-click only its verified window.
function Invoke-SctPreferencePersistence([string]$Package,[string]$Work) {
    Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
    [void][IO.Directory]::CreateDirectory($Work)
    $install = Join-Path $Work 'app'
    [IO.Compression.ZipFile]::ExtractToDirectory($Package,$install)
    $file = Join-Path $Work 'settings-fixture.txt'
    $exe = Join-Path $install 'かわったよ.exe'
    $config = Join-Path $install 'settings.json'
    $id = [guid]::NewGuid().ToString()
    $newName = 'SCT 表示名'
    [IO.File]::WriteAllText($file,'baseline')
    $initial = @{ Version=1; KeepInTray=$false; LowServerLoad=$false; PollSeconds=5; TextSizePoints=10;
        Items=@(@{ Id=$id; Path=$file; IsFolder=$false }) }
    [IO.File]::WriteAllText($config,($initial | ConvertTo-Json -Depth 4))

    $p = Start-SctUnreadApp $exe
    try {
        $main = Wait-SctUi 'baseline before settings edit' {
            $w = Get-SctMonitorWindow $p
            if ($w -and (Get-SctUiLabel $w '最終巡回 ' -Prefix)) { $w }
        }
        [IO.File]::WriteAllText($file,'historical update before settings edit')
        Invoke-SctUiButton $main '↻ 更新'
        [void](Wait-SctUi 'unread update before settings edit' { Get-SctUiLabel $main '未読 1 件' })
        $settings = Open-SctPreferenceWindow $main $p
        $row = Wait-SctUi 'settings fixture target row' { Get-SctUiLabel $settings 'settings-fixture.txt' }
        Show-SctTargetContextMenu $settings $row $p.Id
        $item = Wait-SctUi 'display name action' {
            $element = [Windows.Automation.AutomationElement]
            $condition = [Windows.Automation.AndCondition]::new(
                [Windows.Automation.PropertyCondition]::new($element::NameProperty,'表示名を変更'),
                [Windows.Automation.PropertyCondition]::new($element::ProcessIdProperty,$p.Id))
            $element::RootElement.FindFirst([Windows.Automation.TreeScope]::Descendants,$condition)
        }
        $item.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
        $rename = Wait-SctUi 'rename dialog' { Get-SctOwnedWindow $p.Id '表示名を変更' }
        $edit = $rename.FindFirst([Windows.Automation.TreeScope]::Descendants,
            [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::Edit))
        $edit.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue($newName)
        Invoke-SctUiButton $rename '保存'
        [void](Wait-SctUi 'updated target label' { Get-SctUiLabel $settings $newName })

        $poll = Get-SctPreferenceValue $settings '監視間隔（秒）'
        $size = Get-SctPreferenceValue $settings '文字サイズ（pt）'
        if ($poll.Current.Value -ne 5 -or $size.Current.Value -ne 10) { throw 'The fixture did not load its original UI values.' }
        $poll.SetValue(37)
        $size.SetValue(17)
        [void](Wait-SctUi 'updated numeric values' {
            (Get-SctPreferenceValue $settings '監視間隔（秒）').Current.Value -eq 37 -and
                (Get-SctPreferenceValue $settings '文字サイズ（pt）').Current.Value -eq 17
        })
        $settings.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).Close()
        if (!$p.CloseMainWindow() -or !$p.WaitForExit(7000) -or $p.ExitCode -ne 0) {
            throw 'Graceful fixture exit is required to verify persistence.'
        }
        $saved = [IO.File]::ReadAllText($config) | ConvertFrom-Json
        if ($saved.PollSeconds -ne 37 -or $saved.TextSizePoints -ne 17 -or $saved.Items.Count -ne 1 -or
            $saved.Items[0].Id -ne $id -or $saved.Items[0].Path -cne $file -or $saved.Items[0].DisplayName -cne $newName) {
            throw 'The saved preferences or target identity do not match.'
        }
    } finally { Stop-SctPreferenceFixture $p }

    $p = Start-SctUnreadApp $exe
    try {
        $main = Wait-SctUi 'reopened monitor with renamed unread' {
            $w = Get-SctMonitorWindow $p
            if ($w -and (Get-SctUiLabel $w '未読 1 件') -and (Get-SctUiLabel $w "$newName を既読にする")) { $w }
        }
        Invoke-SctUiButton $main '履歴'
        $history = Wait-SctUi 'reopened history' { Get-SctOwnedWindow $p.Id 'かわったよ | 変更履歴' }
        if (!(Get-SctUiLabel $history '更新')) { throw 'The preexisting update was not restored in history.' }
        $history.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).Close()
        $settings = Open-SctPreferenceWindow $main $p
        if (!(Get-SctUiLabel $settings $newName) -or (Get-SctPreferenceValue $settings '監視間隔（秒）').Current.Value -ne 37 -or
            (Get-SctPreferenceValue $settings '文字サイズ（pt）').Current.Value -ne 17) {
            throw 'Reopened application did not restore edited target and numeric settings.'
        }
    } finally { Stop-SctPreferenceFixture $p }
    [IO.File]::WriteAllText((Join-Path $Work 'observations.txt'),'PASS: display name, interval and text size restored; target ID/path, unread and history retained.')
}

function Open-SctPreferenceWindow($Main,$Process) {
    Invoke-SctUiButton $Main '設定…'
    Wait-SctUi 'fixture settings window' { Get-SctOwnedWindow $Process.Id 'かわったよ | 設定' }
}

function Get-SctPreferenceValue($Settings,[string]$Label) {
    $tab = Get-SctUiLabel $Settings '動作と表示'
    $selection = $tab.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern)
    if (!$selection.Current.IsSelected) { $selection.Select() }
    $anchor = Wait-SctUi "preference label: $Label" { Get-SctUiLabel $Settings $Label }
    $spinner = [Windows.Automation.TreeWalker]::RawViewWalker.GetNextSibling($anchor)
    if (!$spinner -or $spinner.Current.ControlType -ne [Windows.Automation.ControlType]::Spinner) { throw "No spinner adjacent to $Label." }
    $spinner.GetCurrentPattern([Windows.Automation.RangeValuePattern]::Pattern)
}

function Stop-SctPreferenceFixture($Process) {
    try {
        if (!$Process.HasExited -and !($Process.CloseMainWindow() -and $Process.WaitForExit(5000))) {
            $Process.Kill()
            [void]$Process.WaitForExit(5000)
        }
    } finally { $Process.Dispose() }
}

function Show-SctTargetContextMenu($Window,$Row,[int]$PidExpected) {
    if (!('SctTargetMouse' -as [type])) {
        Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;

public static class SctTargetMouse
{
    [StructLayout(LayoutKind.Sequential)] struct Point { public int X; public int Y; }
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out int pid);
    [DllImport("user32.dll")] static extern bool ScreenToClient(IntPtr window, ref Point point);
    [DllImport("user32.dll", SetLastError = true)] static extern bool PostMessage(IntPtr window, uint msg, IntPtr flags, IntPtr data);

    public static bool RightClick(IntPtr window, int pid, int x, int y)
    {
        int actual; GetWindowThreadProcessId(window, out actual);
        var pt = new Point { X = x, Y = y };
        if (actual != pid || !ScreenToClient(window, ref pt) || pt.X < 0 || pt.Y < 0 || pt.X > 32767 || pt.Y > 32767) return false;
        var packed = new IntPtr(pt.X | (pt.Y << 16));
        return PostMessage(window, 0x0204, new IntPtr(2), packed) && PostMessage(window, 0x0205, IntPtr.Zero, packed);
    }
}
"@
    }
    $bounds = $Row.Current.BoundingRectangle
    $frame = $Window.Current.BoundingRectangle
    $x = [int]($bounds.Left + $bounds.Width / 2)
    $y = [int]($bounds.Top + $bounds.Height / 2)
    if ($Row.Current.ProcessId -ne $PidExpected -or $bounds.IsEmpty -or !$frame.Contains($x,$y) -or
        ![SctTargetMouse]::RightClick([IntPtr]$Window.Current.NativeWindowHandle,$PidExpected,$x,$y)) {
        throw 'Could not route the context action to the fixture target.'
    }
}
