# Check the packaged GUI's startup behavior and the bytes left behind, in separate fresh installations.
function Invoke-SctSettingsRecovery([string]$Package,[string]$Work) {
    Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
    [void][IO.Directory]::CreateDirectory($Work)
    foreach ($hasBackup in $true,$false) {
        $app = Join-Path $Work $(if ($hasBackup) { 'with-backup' } else { 'without-backup' })
        [IO.Compression.ZipFile]::ExtractToDirectory($Package, $app)
        Test-SctSettingsStartup $app $hasBackup
    }
    [IO.File]::WriteAllText((Join-Path $Work 'observations.txt'),
        'PASS: valid backup restores settings while preserving original corrupted bytes; no backup fails startup without rewriting the settings.')
}

function Test-SctSettingsStartup([string]$App,[bool]$HasBackup) {
    $settings = Join-Path $App 'settings.json'
    $backup = $settings + '.bak'
    $invalid = '{ invalid settings'
    $valid = '{"Version":1,"PollSeconds":37,"KeepInTray":false,"Items":[]}'
    if (Test-Path -LiteralPath $settings) { throw 'Expected an empty settings fixture.' }
    [IO.File]::WriteAllText($settings, $invalid)
    if ($HasBackup) { [IO.File]::WriteAllText($backup, $valid) }

    $process = Start-SctUnreadApp (Join-Path $App 'かわったよ.exe')
    try {
        $expected = if ($HasBackup) { 'メインの設定を読み込めなかったため、バックアップから復元しました。' }
            else { '設定とバックアップを読み込めません。' }
        $dialog = Wait-SctUi "settings startup message: $expected" {
            if ($process.HasExited) { throw "GUI exited before presenting the startup message: $($process.ExitCode)." }
            $window = Get-SctOwnedWindow $process.Id 'かわったよ'
            if ($window -and (Get-SctUiLabel $window $expected -Prefix)) { $window }
        }
        Confirm-SctNativeSettingsDialog $dialog $process.Id

        if ($HasBackup) {
            [void](Wait-SctUi 'monitor opened with restored settings' {
                $main = Get-SctMonitorWindow $process
                if ($main -and (Get-SctUiLabel $main '未読 0 件')) { $main }
            })
            $data = [IO.File]::ReadAllText($settings) | ConvertFrom-Json
            $broken = @(Get-ChildItem -LiteralPath $App -File -Filter 'settings.json.broken-*')
            if ($data.PollSeconds -ne 37 -or $data.KeepInTray -ne $false -or $broken.Count -ne 1 -or
                [IO.File]::ReadAllText($broken[0].FullName) -cne $invalid -or [IO.File]::ReadAllText($backup) -cne $valid) {
                throw 'Recovered settings or the original/backup bytes do not match.'
            }
        } else {
            if (!$process.WaitForExit(10000) -or $process.ExitCode -ne 1) {
                throw 'GUI should fail startup when the corrupt settings have no valid backup.'
            }
            if ([IO.File]::ReadAllText($settings) -cne $invalid -or [IO.File]::Exists($backup)) {
                throw 'Failed startup modified the original settings or produced a backup.'
            }
        }
    } finally {
        try {
            if (!$process.HasExited -and !($process.CloseMainWindow() -and $process.WaitForExit(5000))) {
                $process.Kill()
                [void]$process.WaitForExit(5000)
            }
        } finally { $process.Dispose() }
    }
}

function Confirm-SctNativeSettingsDialog($Dialog,[int]$ProcessId) {
    if (!('SctSettingsDialogNative' -as [type])) {
        Add-Type -TypeDefinition @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class SctSettingsDialogNative
{
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out int pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder text, int limit);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    public static string Confirm(IntPtr dialog, int expectedProcess)
    {
        int pid;
        GetWindowThreadProcessId(dialog, out pid);
        var cls = new StringBuilder(32);
        GetClassName(dialog, cls, cls.Capacity);
        if (pid != expectedProcess || cls.ToString() != "#32770") return "unexpected dialog, handle=" + dialog + "; process=" + pid + "; class=" + cls;
        // The message is informational; clicking the title-bar close action dismisses the same dialog.
        return PostMessage(dialog, 0x0010, IntPtr.Zero, IntPtr.Zero) ? null : "PostMessage failed, error=" + Marshal.GetLastWin32Error();
    }
}
"@
    }
    $handle = [IntPtr]$Dialog.Current.NativeWindowHandle
    $failure = [SctSettingsDialogNative]::Confirm($handle,$ProcessId)
    if ($failure) { throw "Could not dismiss the verified fixture message box: $failure." }
}
