# Drive native pickers on the test executable; drag-and-drop is a separate desktop case.
function Invoke-SctPickerRegistration([string]$Package,[string]$Work) {
    Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
    [void][IO.Directory]::CreateDirectory($Work)
    $install = Join-Path $Work 'app'
    [IO.Compression.ZipFile]::ExtractToDirectory($Package,$install)
    $folder = Join-Path $Work '監視 フォルダ'
    $file = Join-Path $Work '測定 ファイル.txt'
    [void][IO.Directory]::CreateDirectory($folder)
    [IO.File]::WriteAllText($file,'fixture')
    $config=Join-Path $install 'settings.json'
    [IO.File]::WriteAllText($config,'{"Version":1,"KeepInTray":false,"LowServerLoad":false,"PollSeconds":5,"Items":[]}')
    $p = Start-SctUnreadApp (Join-Path $install 'かわったよ.exe')
    try {
        $main = Wait-SctUi 'monitor before picker' { Get-SctMonitorWindow $p }
        $settings = Open-SctPreferenceWindow $main $p
        Invoke-SctTargetPicker $p $settings $file -File
        [void](Wait-SctUi 'file appears in settings' { Get-SctUiLabel $settings ([IO.Path]::GetFileName($file)) })
        Invoke-SctTargetPicker $p $settings $folder
        [void](Wait-SctUi 'folder appears in settings' { Get-SctUiLabel $settings ([IO.Path]::GetFileName($folder)) })
        $before = Wait-SctUi 'both registrations persisted' {
            $stored = [IO.File]::ReadAllText($config) | ConvertFrom-Json
            if (@($stored.Items).Count -eq 2) { $stored }
        }
        Invoke-SctTargetPicker $p $settings $file -File
        Invoke-SctTargetPicker $p $settings $folder
        $settings.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).Close()
        # Wait for another completed scan before shutdown, so registration callbacks can finish.
        $prior = (Get-SctUiLabel $main '最終巡回 ' -Prefix).Current.Name
        Invoke-SctUiButton $main '↻ 更新'
        [void](Wait-SctUi 'scan completed after repeats' {
            $stamp = Get-SctUiLabel $main '最終巡回 ' -Prefix
            $stamp -and $stamp.Current.Name -ne $prior
        })
        if (!$p.CloseMainWindow() -or !$p.WaitForExit(7000) -or $p.ExitCode -ne 0) { throw 'Fixture did not exit gracefully.' }
        $after = [IO.File]::ReadAllText($config) | ConvertFrom-Json
        foreach ($settingsData in $before,$after) {
            if (@($settingsData.Items).Count -ne 2) { throw "Expected two registrations, got $(@($settingsData.Items).Count)." }
            $folders = @($settingsData.Items | Where-Object IsFolder)
            $files = @($settingsData.Items | Where-Object { !$_.IsFolder })
            if ($folders.Count -ne 1 -or $files.Count -ne 1 -or $folders[0].Path -cne $folder -or $files[0].Path -cne $file) {
                throw 'The saved kinds or paths do not match the picked targets.'
            }
        }
        foreach ($entry in $before.Items) {
            if (@($after.Items | Where-Object { $_.Id -and $_.Id -eq $entry.Id -and $_.Path -ceq $entry.Path }).Count -ne 1) {
                throw 'A repeated selection must not replace the existing identity.'
            }
        }
        [IO.File]::WriteAllText((Join-Path $Work 'observations.txt'),'PASS: both native pickers registered exactly one target of the correct type; repeat selection preserved both IDs.')
    } finally { Stop-SctPreferenceFixture $p }
}

function Invoke-SctTargetPicker($Process,$Settings,[string]$Path,[switch]$File) {
    $menuName = if ($File) { 'ファイルを選択…' } else { 'フォルダを選択…' }
    $title = if ($File) { '監視するファイルを選択' } else { '監視するフォルダを選択' }
    Invoke-SctUiButton $Settings '＋ 追加 ▾'
    $action = Wait-SctUi "picker menu: $menuName" {
        $element = [Windows.Automation.AutomationElement]
        $condition = [Windows.Automation.AndCondition]::new(
            [Windows.Automation.PropertyCondition]::new($element::NameProperty,$menuName),
            [Windows.Automation.PropertyCondition]::new($element::ProcessIdProperty,$Process.Id))
        $element::RootElement.FindFirst([Windows.Automation.TreeScope]::Descendants,$condition)
    }
    $action.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    $picker = Wait-SctUi "picker: $title" { Get-SctOwnedWindow $Process.Id $title }
    Submit-SctNativePicker $picker $Process.Id $Path -FilePicker:$File
    if (!$File) {
        $timer = [Diagnostics.Stopwatch]::StartNew()
        while ($timer.Elapsed.TotalSeconds -lt 4) {
            $current = Get-SctOwnedWindow $Process.Id $title
            if (!$current) { break }
            $condition = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,'1001')
            $address = $current.FindFirst([Windows.Automation.TreeScope]::Descendants,$condition)
            if ($address -and $address.Current.Name.EndsWith($Path,[StringComparison]::OrdinalIgnoreCase)) {
                Submit-SctNativePicker $current $Process.Id $Path
                break
            }
            Start-Sleep -Milliseconds 150
        }
    }
    [void](Wait-SctUi "picker accepted: $title" { !(Get-SctOwnedWindow $Process.Id $title) })
}
