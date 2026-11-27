# Clear disappearance notifications without erasing other unread rows or history.
function Invoke-SctClearDeleted([string]$Package,[string]$Work) {
    Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
    [void][IO.Directory]::CreateDirectory($Work)
    $install = Join-Path $Work 'product'
    [IO.Compression.ZipFile]::ExtractToDirectory($Package,$install)
    $exe = Join-Path $install 'かわったよ.exe'
    $folder = Join-Path $Work 'folder-inbox'
    [void][IO.Directory]::CreateDirectory($folder)
    $gone = Join-Path $Work 'gone-file.txt'
    $update = Join-Path $Work 'keep-update.txt'
    $childGone = Join-Path $folder 'gone-child.txt'
    $childNew = Join-Path $folder 'keep-new.txt'
    foreach ($path in $gone,$update,$childGone) { [IO.File]::WriteAllText($path,'baseline') }
    $settings = @{ Version=1;KeepInTray=$false;LowServerLoad=$false;PollSeconds=5;NotifyDeleted=$true;WaitForStable=$false;
        Items=@(@{Id=[guid]::NewGuid().ToString();Path=$folder;IsFolder=$true;Recursive=$true},
            @{Id=[guid]::NewGuid().ToString();Path=$gone;IsFolder=$false},
            @{Id=[guid]::NewGuid().ToString();Path=$update;IsFolder=$false}) }
    [IO.File]::WriteAllText((Join-Path $install 'settings.json'),($settings | ConvertTo-Json -Depth 4))

    $p = Start-SctUnreadApp $exe
    try {
        $view = Wait-SctUi 'initial scan for disappearance test' {
            $main = Get-SctMonitorWindow $p
            if ($main -and (Get-SctUiLabel $main '最終巡回 ' -Prefix) -and (Get-SctUiLabel $main '未読 0 件')) { $main }
        }
        [IO.File]::Delete($gone)
        [IO.File]::Delete($childGone)
        [IO.File]::WriteAllText($update,'new contents for the existing file')
        [IO.File]::WriteAllText($childNew,'brand new contents')
        Invoke-SctUiButton $view '↻ 更新'
        [void](Wait-SctUi 'two disappearances and two unrelated changes' { Get-SctUiLabel $view '未読 4 件' })
        Invoke-SctUiButton $view 'すべて展開'
        [void](Wait-SctUi 'expected rows before clearing' {
            (Get-SctUiLabel $view 'gone-file.txt を既読にする') -and (Get-SctUiLabel $view 'gone-child.txt を既読にする') -and
            (Get-SctUiLabel $view 'keep-update.txt を既読にする') -and (Get-SctUiLabel $view 'keep-new.txt を既読にする')
        })
        Invoke-SctUiButton $view '消失通知を消す'
        [void](Confirm-SctNonDeletedRows $view)
        # A second invocation is harmless.
        Invoke-SctUiButton $view '消失通知を消す'
        [void](Confirm-SctNonDeletedRows $view)
        if (!$p.CloseMainWindow() -or !$p.WaitForExit(7000) -or $p.ExitCode -ne 0) { throw 'Expected clean fixture exit.' }
    } finally { Stop-SctPreferenceFixture $p }

    $p = Start-SctUnreadApp $exe
    try {
        $view = Wait-SctUi 'the two unrelated unread items persisted' {
            $main = Get-SctMonitorWindow $p
            if ($main -and (Get-SctUiLabel $main '最終巡回 ' -Prefix) -and (Get-SctUiLabel $main '未読 2 件')) { $main }
        }
        Invoke-SctUiButton $view 'すべて展開'
        [void](Confirm-SctNonDeletedRows $view)
        Invoke-SctUiButton $view '履歴'
        [void](Wait-SctUi 'all original history rows remain' {
            $history = Get-SctOwnedWindow $p.Id 'かわったよ | 変更履歴'
            if (!$history) { return $false }
            (Get-SctUiLabel $history $gone) -and (Get-SctUiLabel $history $childGone) -and
            (Get-SctUiLabel $history $update) -and (Get-SctUiLabel $history $childNew)
        })
        if ([IO.File]::Exists($gone) -or [IO.File]::Exists($childGone) -or [IO.File]::ReadAllText($update) -cne 'new contents for the existing file' -or
            [IO.File]::ReadAllText($childNew) -cne 'brand new contents') { throw 'Clearing notifications changed file contents or existence.' }
    } finally { Stop-SctPreferenceFixture $p }
    [IO.File]::WriteAllText((Join-Path $Work 'observations.txt'),
        'PASS: only disappearance unread rows were cleared; repeated clearing and restart preserved other unread entries; all history and filesystem state retained.')
}

function Confirm-SctNonDeletedRows($View) {
    Wait-SctUi 'only non-deletion unread entries remain' {
        (Get-SctUiLabel $View '未読 2 件') -and (Get-SctUiLabel $View 'keep-update.txt を既読にする') -and
        (Get-SctUiLabel $View 'keep-new.txt を既読にする') -and !(Get-SctUiLabel $View 'gone-file.txt を既読にする') -and
        !(Get-SctUiLabel $View 'gone-child.txt を既読にする')
    }
}
