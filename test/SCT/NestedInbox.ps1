# Exercise nested unread branches that contain two different files with the same leaf name.
function Invoke-SctNestedInbox([string]$Package,[string]$Work) {
    Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
    [void][IO.Directory]::CreateDirectory($Work)
    $install = Join-Path $Work 'app'
    [IO.Compression.ZipFile]::ExtractToDirectory($Package,$install)
    $exe = Join-Path $install 'かわったよ.exe'
    $root = Join-Path $Work 'NestedInbox'
    $left = Join-Path $root 'Left/L2'
    $right = Join-Path $root 'Right/R2'
    foreach ($folder in $left,$right) { [void][IO.Directory]::CreateDirectory($folder) }
    $settings = @{Version=1; KeepInTray=$false; LowServerLoad=$false; PollSeconds=5; WaitForStable=$false;
        Items=@(@{Id=[guid]::NewGuid().ToString();Path=$root;IsFolder=$true;Recursive=$true;RecursiveOverride=$true})}
    [IO.File]::WriteAllText((Join-Path $install 'settings.json'),($settings | ConvertTo-Json -Depth 4))

    $p = Start-SctUnreadApp $exe
    try {
        $view = Wait-SctUi 'initial nested inbox scan' {
            $main = Get-SctMonitorWindow $p
            if ($main -and (Get-SctUiLabel $main '最終巡回 ' -Prefix) -and (Get-SctUiLabel $main '未読 0 件')) { $main }
        }
        $leftFile = Join-Path $left 'shared.txt'
        $rightFile = Join-Path $right 'shared.txt'
        [IO.File]::WriteAllText($leftFile,'left branch contents')
        [IO.File]::WriteAllText($rightFile,'right branch contents')
        Invoke-SctUiButton $view '↻ 更新'
        [void](Wait-SctUi 'both branch edits but only root visible' {
            (Get-SctUiLabel $view '未読 2 件') -and !(Get-SctUiLabel $view 'Left') -and !(Get-SctUiLabel $view 'Right')
        })

        Invoke-SctUiButton $view 'すべて展開'
        $leaves = @(Wait-SctUi 'deep branches expanded' {
            if ((Get-SctUiLabel $view 'L2') -and (Get-SctUiLabel $view 'R2')) {
                $buttons = @(Get-SctSameLeafButtons $view)
                if ($buttons.Count -eq 2) { $buttons }
            }
        })
        Invoke-SctUiButton $view 'すべて折りたたむ'
        [void](Wait-SctUi 'collapse retains both unread and hides children' {
            (Get-SctUiLabel $view '未読 2 件') -and !(Get-SctUiLabel $view 'L2') -and !(Get-SctUiLabel $view 'R2') -and
                @(Get-SctSameLeafButtons $view).Count -eq 0
        })
        Invoke-SctUiButton $view 'すべて展開'
        $leaves = @(Wait-SctUi 'both leaves returned in vertical order' {
            $buttons = @(Get-SctSameLeafButtons $view | Sort-Object { $_.Current.BoundingRectangle.Top })
            if ($buttons.Count -eq 2 -and $buttons[0].Current.BoundingRectangle.Top -lt $buttons[1].Current.BoundingRectangle.Top) { $buttons }
        })
        $leaves[0].GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
        [void](Wait-SctUi 'only right branch still unread for the same basename' {
            (Get-SctUiLabel $view '未読 1 件') -and !(Get-SctUiLabel $view 'L2') -and (Get-SctUiLabel $view 'R2') -and
                @(Get-SctSameLeafButtons $view).Count -eq 1
        })
        if ([IO.File]::ReadAllText($leftFile) -cne 'left branch contents' -or [IO.File]::ReadAllText($rightFile) -cne 'right branch contents') {
            throw 'Collapsing or acknowledging modified a watched file.'
        }
    } finally { Stop-SctPreferenceFixture $p }
    [IO.File]::WriteAllText((Join-Path $Work 'observations.txt'),
        'PASS: expand and collapse preserved both updates, and acknowledging a same-name file removed only the left branch.')
}

function Get-SctSameLeafButtons($Window) {
    $el = [Windows.Automation.AutomationElement]
    $condition = [Windows.Automation.AndCondition]::new(
        [Windows.Automation.PropertyCondition]::new($el::NameProperty,'shared.txt を既読にする'),
        [Windows.Automation.PropertyCondition]::new($el::ControlTypeProperty,[Windows.Automation.ControlType]::Button))
    $Window.FindAll([Windows.Automation.TreeScope]::Descendants,$condition)
}
