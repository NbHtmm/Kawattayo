# A unique unpacked executable profile gets its own hook configuration and recorder.
function Invoke-SctUpdateHook([string]$Package,[string]$Work) {
    Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
    [void][IO.Directory]::CreateDirectory($Work)
    $install = Join-Path $Work 'app'
    [IO.Compression.ZipFile]::ExtractToDirectory($Package,$install)
    $recorder = Join-Path $Work 'Hook Recorder.exe'
    $log = Join-Path $Work 'invocations.txt'
    $localSettings = Get-SctHookRulePath $install
    if ([IO.File]::Exists($localSettings)) { throw 'This test requires a fresh hook profile.' }
    $build = Join-Path $PSScriptRoot 'Fixtures/BuildHookRecorder.ps1'
    & powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $build -Output $recorder
    if ($LASTEXITCODE -ne 0 -or ![IO.File]::Exists($recorder)) { throw 'Could not build the test-only recorder.' }
    $target = Join-Path $Work '測定 01.txt'
    [IO.File]::WriteAllText($target,'initial')
    $id = [guid]::NewGuid().ToString()
    $settings = @{ Version=1; KeepInTray=$false; LowServerLoad=$false; WaitForStable=$false; PollSeconds=5;
        Items=@(@{ Id=$id; Path=$target; DisplayName='SCT Hook Target'; IsFolder=$false }) }
    [IO.File]::WriteAllText((Join-Path $install 'settings.json'),($settings | ConvertTo-Json -Depth 4))
    $p = Start-SctUnreadApp (Join-Path $install 'かわったよ.exe')
    $ownedRule = $false
    try {
        $main = Wait-SctUi 'completed hook baseline' {
            $w = Get-SctMonitorWindow $p
            if ($w -and (Get-SctUiLabel $w '最終巡回 ' -Prefix)) { $w }
        }
        $settings = Open-SctPreferenceWindow $main $p
        $dialog = Open-SctHookDialog $settings $p
        $toggle = (Get-SctUiLabel $dialog '追加・更新時に実行する').GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern)
        if ($toggle.Current.ToggleState -ne [Windows.Automation.ToggleState]::Off) { throw 'A fresh profile should start without a hook.' }
        $toggle.Toggle()
        $editors = @($dialog.FindAll([Windows.Automation.TreeScope]::Descendants,
            [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::Edit)) |
            Where-Object { $_.Current.ClassName -eq 'TextBox' -and $_.Current.AutomationId -ne 'PART_TextBox' })
        if ($editors.Count -ne 2) { throw 'Could not identify both hook setup editors.' }
        $editors[0].GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue($recorder)
        $fixedArgument = '日本語 固定引数'
        $editors[1].GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue($fixedArgument)
        $anchor = Get-SctUiLabel $dialog '安定待ち（秒、0で待たない）'
        $spinner = [Windows.Automation.TreeWalker]::RawViewWalker.GetNextSibling($anchor)
        $spinner.GetCurrentPattern([Windows.Automation.RangeValuePattern]::Pattern).SetValue(0)
        $ownedRule = $true # The fresh profile has one test-owned command, even if save is interrupted.
        Invoke-SctUiButton $dialog '保存'
        [void](Wait-SctUi 'saved test hook' { [IO.File]::Exists($localSettings) })
        if ([IO.File]::Exists($log)) { throw 'Hook ran before a file update.' }
        $settings.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).Close()
        [IO.File]::WriteAllText($target,'this update is observable')
        Invoke-SctUiButton $main '↻ 更新'

        $observed = Wait-SctUi 'recorded hook invocation' {
            if ([IO.File]::Exists($log)) { [IO.File]::ReadAllText($log) }
        }
        $lines = @($observed.TrimEnd() -split "\r?\n")
        $values = @($lines[0].Split('|') | ForEach-Object { [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($_)) })
        if ($lines.Count -ne 1 -or $values.Count -ne 2 -or $values[0] -cne $fixedArgument -or $values[1] -cne $target) {
            throw 'The hook did not receive exactly one fixed argument plus one literal target argument.'
        }
        $settings = Open-SctPreferenceWindow $main $p
        $dialog = Open-SctHookDialog $settings $p
        [void](Wait-SctUi 'successful process exit reported by GUI' {
            Get-SctUiLabel $dialog ("終了コード 0："+$target)
        })
        if ([IO.File]::ReadAllText($target) -cne 'this update is observable') { throw 'Hook modified the source.' }
        Invoke-SctUiButton $dialog 'キャンセル'
        [IO.File]::WriteAllText((Join-Path $Work 'observations.txt'),'PASS: one process received the Unicode, space-containing path as a single argument; exit code 0 was reported.')
    } finally {
        try { Stop-SctPreferenceFixture $p }
        finally {
            # Delete only the one local rule file whose key identifies this exact freshly unpacked profile.
            if ($ownedRule -and [IO.File]::Exists($localSettings)) { [IO.File]::Delete($localSettings) }
        }
    }
}

function Get-SctHookRulePath([string]$Profile) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $key = [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes([IO.Path]::GetFullPath($Profile).ToUpperInvariant()))).Replace('-','') }
    finally { $sha.Dispose() }
    Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) "Kawattayo/Hooks/$key.json"
}

function Open-SctHookDialog($Settings,$Process) {
    $row = Wait-SctUi 'hook test target row' { Get-SctUiLabel $Settings 'SCT Hook Target' }
    Show-SctTargetContextMenu $Settings $row $Process.Id
    $action = Wait-SctUi 'hook test context action' {
        $element = [Windows.Automation.AutomationElement]
        $condition = [Windows.Automation.AndCondition]::new(
            [Windows.Automation.PropertyCondition]::new($element::NameProperty,'更新フック…'),
            [Windows.Automation.PropertyCondition]::new($element::ProcessIdProperty,$Process.Id))
        $element::RootElement.FindFirst([Windows.Automation.TreeScope]::Descendants,$condition)
    }
    $action.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    Wait-SctUi 'hook configuration dialog' {
        Get-SctOwnedWindow $Process.Id '更新フック ｜ SCT Hook Target'
    }
}
