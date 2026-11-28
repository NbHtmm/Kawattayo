# A registration's explicit recursion choice overrides the global default until reset.
function Invoke-SctRecursionOverride([string]$Package,[string]$Work) {
    Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
    [void][IO.Directory]::CreateDirectory($Work)
    $install = Join-Path $Work 'app'
    [IO.Compression.ZipFile]::ExtractToDirectory($Package,$install)
    $exe = Join-Path $install 'かわったよ.exe'
    $config = Join-Path $install 'settings.json'
    $root = Join-Path $Work 'WatchOverride'
    $sub = Join-Path $root 'sub'
    [void][IO.Directory]::CreateDirectory($sub)
    $nested = Join-Path $sub 'nested.txt'
    $top = Join-Path $root 'top.txt'
    foreach ($path in $top,$nested) { [IO.File]::WriteAllText($path,'baseline') }
    $target = @{ Id=[guid]::NewGuid().ToString();Path=$root;IsFolder=$true;Recursive=$false }
    $initial = @{ Version=1;KeepInTray=$false;LowServerLoad=$false;RecursiveFolders=$false;
        PollSeconds=5;Items=@($target) }
    [IO.File]::WriteAllText($config,($initial | ConvertTo-Json -Depth 4))

    $p = Start-SctUnreadApp $exe
    try {
        $main = Wait-SctUi 'initial folder query' {
            $w = Get-SctMonitorWindow $p
            if ($w -and (Get-SctUiLabel $w '最終巡回 ' -Prefix) -and (Get-SctUiLabel $w '未読 0 件')) { $w }
        }
        $settings = Open-SctPreferenceWindow $main $p
        Set-SctRecursionOverrideFromMenu $settings $p '再帰監視：オン' 'ON'
        [void](Wait-SctUi 'two files with override enabled' {
            (Get-SctUiLabel $settings '2') -and (Get-SctUiLabel $main '未読 0 件')
        })
        [IO.File]::WriteAllText($nested,'new nested file contents under explicit override')
        Invoke-SctUiButton $main '↻ 更新'
        [void](Wait-SctUi 'nested change detected with override enabled' { Get-SctUiLabel $main '未読 1 件' })
        Invoke-SctUiButton $main 'すべて展開'
        [void](Wait-SctUi 'nested file is the unread row' { Get-SctUiLabel $main 'nested.txt を既読にする' })
        Invoke-SctUiButton $main 'WatchOverride を既読にする'
        [void](Wait-SctUi 'acknowledged nested update' { Get-SctUiLabel $main '未読 0 件' })

        Set-SctRecursionOverrideFromMenu $settings $p '再帰監視：既定に戻す' 'デフォルト'
        [void](Wait-SctUi 'back to one direct file and no phantom deletion' {
            (Get-SctUiLabel $settings '1') -and (Get-SctUiLabel $main '未読 0 件')
        })
        [IO.File]::WriteAllText($nested,'later nested update must stay outside inherited scope')
        [IO.File]::WriteAllText($top,'this top-level update must be detected')
        Invoke-SctUiButton $main '↻ 更新'
        [void](Wait-SctUi 'only direct file now unread' { Get-SctUiLabel $main '未読 1 件' })
        Invoke-SctUiButton $main 'すべて展開'
        [void](Wait-SctUi 'expanded only expected file' {
            (Get-SctUiLabel $main 'top.txt を既読にする') -and !(Get-SctUiLabel $main 'nested.txt を既読にする')
        })
        $settings.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).Close()
        if (!$p.CloseMainWindow() -or !$p.WaitForExit(7000) -or $p.ExitCode -ne 0) {
            throw 'Expected normal fixture exit.'
        }
        $saved = [IO.File]::ReadAllText($config) | ConvertFrom-Json
        if ($saved.RecursiveFolders -ne $false -or $saved.Items.Count -ne 1 -or
            $saved.Items[0].Id -ne $target.Id -or $saved.Items[0].RecursiveOverride -ne $null) {
            throw 'The app saved the wrong overrides or changed the global default or registration identity.'
        }
    } finally { Stop-SctPreferenceFixture $p }
    [IO.File]::WriteAllText((Join-Path $Work 'observations.txt'),
        'PASS: local override enabled nested monitoring without modifying the default; reset stopped nested monitoring without causing an unread deletion.')
}

function Set-SctRecursionOverrideFromMenu($Settings,$Process,[string]$Command,[string]$Expected) {
    $row = Wait-SctUi 'recursion setting fixture row' { Get-SctUiLabel $Settings 'WatchOverride' }
    Show-SctTargetContextMenu $Settings $row $Process.Id
    $element = [Windows.Automation.AutomationElement]
    $item = Wait-SctUi ('recursion menu: ' + $Command) {
        $condition = [Windows.Automation.AndCondition]::new(
            [Windows.Automation.PropertyCondition]::new($element::NameProperty,$Command),
            [Windows.Automation.PropertyCondition]::new($element::ProcessIdProperty,$Process.Id))
        $element::RootElement.FindFirst([Windows.Automation.TreeScope]::Descendants,$condition)
    }
    $item.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    [void](Wait-SctUi ('updated recursion display: ' + $Expected) { Get-SctUiLabel $Settings $Expected })
}
