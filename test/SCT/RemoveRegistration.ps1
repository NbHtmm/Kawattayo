# Remove a single registration from the isolated packaged GUI without mutating the watched files.
function Invoke-SctRemoveRegistration([string]$Package,[string]$Work) {
    Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
    [void][IO.Directory]::CreateDirectory($Work)
    $install = Join-Path $Work 'app'
    [IO.Compression.ZipFile]::ExtractToDirectory($Package,$install)
    $exe = Join-Path $install 'かわったよ.exe'
    $config = Join-Path $install 'settings.json'
    $removed = Join-Path $Work 'remove-me.txt'
    $retained = Join-Path $Work 'keep-me.txt'
    $retainedId = [guid]::NewGuid().ToString()
    foreach ($path in $removed,$retained) { [IO.File]::WriteAllText($path,'baseline') }
    $initial = @{ Version=1; KeepInTray=$false; LowServerLoad=$false; PollSeconds=5;
        Items=@(@{ Id=[guid]::NewGuid().ToString(); Path=$removed; IsFolder=$false },
            @{ Id=$retainedId; Path=$retained; IsFolder=$false }) }
    [IO.File]::WriteAllText($config,($initial | ConvertTo-Json -Depth 4))

    $p = Start-SctUnreadApp $exe
    try {
        $main = Wait-SctUi 'initial scan of two registrations' {
            $w = Get-SctMonitorWindow $p
            if ($w -and (Get-SctUiLabel $w '最終巡回 ' -Prefix)) { $w }
        }
        foreach ($path in $removed,$retained) { [IO.File]::WriteAllText($path,'first changed contents') }
        Invoke-SctUiButton $main '↻ 更新'
        [void](Wait-SctUi 'both updates shown' { Get-SctUiLabel $main '未読 2 件' })
        $settings = Open-SctPreferenceWindow $main $p

        Invoke-SctRegistrationConfirmation $settings $p '7'
        if (!(Get-SctUiLabel $settings 'remove-me.txt') -or !(Get-SctUiLabel $main '未読 2 件')) {
            throw 'Declining removal incorrectly changed the registered or unread state.'
        }
        Invoke-SctRegistrationConfirmation $settings $p '6'
        [void](Wait-SctUi 'only the unremoved registration remains' {
            !(Get-SctUiLabel $settings 'remove-me.txt') -and (Get-SctUiLabel $settings 'keep-me.txt') -and
                (Get-SctUiLabel $main '未読 1 件') -and (Get-SctUiLabel $main 'keep-me.txt を既読にする')
        })
        $settings.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).Close()
        if (!$p.CloseMainWindow() -or !$p.WaitForExit(7000) -or $p.ExitCode -ne 0) {
            throw 'Graceful fixture exit is required to verify registration persistence.'
        }
        $saved = [IO.File]::ReadAllText($config) | ConvertFrom-Json
        if ($saved.Items.Count -ne 1 -or $saved.Items[0].Id -ne $retainedId -or $saved.Items[0].Path -cne $retained) {
            throw 'The removed registration was persisted or the retained registration changed identity.'
        }
        foreach ($path in $removed,$retained) {
            if ([IO.File]::ReadAllText($path) -cne 'first changed contents') { throw "The application modified a watched file: $path." }
        }
    } finally { Stop-SctPreferenceFixture $p }

    $p = Start-SctUnreadApp $exe
    try {
        $main = Wait-SctUi 'reopened monitor with one unread' {
            $w = Get-SctMonitorWindow $p
            if ($w -and (Get-SctUiLabel $w '未読 1 件') -and (Get-SctUiLabel $w 'keep-me.txt を既読にする') -and
                !(Get-SctUiLabel $w 'remove-me.txt を既読にする')) { $w }
        }
        Invoke-SctUiButton $main '履歴'
        [void](Wait-SctUi 'both chronological records retained' {
            $w = Get-SctOwnedWindow $p.Id 'かわったよ | 変更履歴'
            if ($w -and (Get-SctUiLabel $w $removed) -and (Get-SctUiLabel $w $retained)) { $w }
        })
    } finally { Stop-SctPreferenceFixture $p }
    [IO.File]::WriteAllText((Join-Path $Work 'observations.txt'),
        'PASS: No retained both entries; Yes removed only its registration after restart. The other unread entry, both history rows and both original files were unchanged.')
}

function Invoke-SctRegistrationConfirmation($Settings,$Process,[string]$ButtonId,[string]$TargetName = 'remove-me.txt') {
    if (!('SctRegistrationDialog' -as [type])) {
        Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;

public static class SctRegistrationDialog
{
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out int pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr window, StringBuilder text, int limit);
    [DllImport("user32.dll")] static extern IntPtr GetDlgItem(IntPtr dialog, int id);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr window, uint message, IntPtr word, IntPtr parameter);

    public static bool Choose(IntPtr dialog, int expectedProcess, int id)
    {
        GetWindowThreadProcessId(dialog, out int pid);
        var type = new StringBuilder(32);
        GetClassName(dialog, type, type.Capacity);
        if (pid != expectedProcess || type.ToString() != "#32770" || (id != 6 && id != 7)) return false;
        IntPtr button = GetDlgItem(dialog, id);
        return button != IntPtr.Zero && PostMessage(dialog, 0x0111, new IntPtr(id), button);
    }
}
"@
    }
    $row = Wait-SctUi 'fixture removal row' { Get-SctUiLabel $Settings $TargetName }
    Show-SctTargetContextMenu $Settings $row $Process.Id
    $element = [Windows.Automation.AutomationElement]
    $descendants = [Windows.Automation.TreeScope]::Descendants
    $menu = Wait-SctUi 'remove registration action' {
        $condition = [Windows.Automation.AndCondition]::new(
            [Windows.Automation.PropertyCondition]::new($element::NameProperty,'登録解除'),
            [Windows.Automation.PropertyCondition]::new($element::ProcessIdProperty,$Process.Id))
        $element::RootElement.FindFirst($descendants,$condition)
    }
    # The UIA menu action waits for the native message box. Start it in a separate runspace
    # so this thread can handle that dialog without sending desktop-wide input.
    [void](Get-SctOwnedWindow $Process.Id 'かわったよ | 設定') # Load the fixture-only window finder before invoking.
    $worker = [PowerShell]::Create()
    $invoker = $menu.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern)
    [void]$worker.AddScript('param($pattern) $ErrorActionPreference="Stop"; $pattern.Invoke()').AddArgument($invoker)
    $pending = $worker.BeginInvoke()
    try {
        # Resolve only the fixture PID/title. Avoid UIA calls while its menu provider is blocked in Invoke.
        $title = 'かわったよ'
        $dialog = Wait-SctUi 'fixture confirmation dialog' {
            $handle = [SctOwnedNative]::Find($Process.Id,$title,$false)
            if ($handle -ne [IntPtr]::Zero) { $handle }
        }
        if (![SctRegistrationDialog]::Choose($dialog,$Process.Id,[int]$ButtonId)) {
            throw "Missing verified confirmation button: $ButtonId."
        }
        [void](Wait-SctUi 'confirmation dismissed' {
            [SctOwnedNative]::Find($Process.Id,$title,$false) -eq [IntPtr]::Zero
        })
        if (!$pending.AsyncWaitHandle.WaitOne(5000)) { throw 'The menu did not return after confirmation.' }
        [void]$worker.EndInvoke($pending)
    } finally {
        if (!$pending.IsCompleted -and !$Process.HasExited) {
            $Process.Kill() # The isolated fixture owns this process, not an existing installation.
            [void]$Process.WaitForExit(5000)
        }
        if ($pending.AsyncWaitHandle.WaitOne(2000)) { $worker.Dispose() }
    }
}
