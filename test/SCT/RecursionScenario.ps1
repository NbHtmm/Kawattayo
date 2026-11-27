function Test-SctRecursion($Process,$View,[string]$Root,[string]$Top,[string]$Evidence) {
    $nested = Join-Path $Root 'depth/deep.txt'
    [IO.File]::WriteAllText($nested,'not tracked yet, changed')
    [IO.File]::WriteAllText($Top,'tracked top change')
    Confirm-SctSingleFolderUpdate $Process $View 'lifecycle.txt'

    Invoke-SctUiButton $View '設定…'
    $settings = Wait-SctUi 'settings window' { Get-SctOwnedWindow $Process.Id 'かわったよ | 設定' }
    $tab = Get-SctUiLabel $settings '動作と表示'
    $select = $null
    if (!$tab -or !$tab.TryGetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern,[ref]$select)) {
        throw 'Could not select the preferences tab.'
    }
    $select.Select()
    $option = Wait-SctUi 'default recursion checkbox' {
        Get-SctUiLabel $settings 'デフォルトでサブフォルダも再帰的に監視'
    }
    $toggle = $null
    if (!$option.TryGetCurrentPattern([Windows.Automation.TogglePattern]::Pattern,[ref]$toggle)) {
        throw 'Could not toggle the recursion option.'
    }

    Set-SctRecursion $Process $View $toggle 'Off' 'On'
    [IO.File]::WriteAllText($nested,'now tracked, a new distinct value')
    Confirm-SctSingleFolderUpdate $Process $View 'deep.txt'

    Set-SctRecursion $Process $View $toggle 'On' 'Off'
    [IO.File]::WriteAllText($nested,'second ignored change after turning recursion off')
    [IO.File]::WriteAllText($Top,'new top-level update after turning recursion off')
    Confirm-SctSingleFolderUpdate $Process $View 'lifecycle.txt'
    [IO.File]::WriteAllText($Evidence,'PASS: nonrecursive excludes lower-level changes; toggling either way does not generate notifications; only new nested changes are found while enabled.')
}

function Set-SctRecursion($Process,$View,$Toggle,[string]$Before,[string]$After) {
    if ($Toggle.Current.ToggleState.ToString() -ne $Before) { throw "Unexpected recursion state before toggle: $Before." }
    $prior = (Get-SctUiLabel $View '最終巡回 ' -Prefix).Current.Name
    $Toggle.Toggle()
    [void](Wait-SctUi "completed rebaseline for recursion $After" {
        $main = Get-SctMonitorWindow $Process
        $status = if ($main) { Get-SctUiLabel $main '最終巡回 ' -Prefix }
        $Toggle.Current.ToggleState.ToString() -eq $After -and $status -and $status.Current.Name -ne $prior
    })
    if (!(Get-SctUiLabel $View '未読 0 件')) { throw 'Changing recursion incorrectly generated unread events.' }
}

function Confirm-SctSingleFolderUpdate($Process,$View,[string]$Name) {
    Invoke-SctUiButton $View '↻ 更新'
    [void](Wait-SctUi "single unread update for $Name" {
        $main = Get-SctMonitorWindow $Process
        if ($main -and (Get-SctUiLabel $main '未読 1 件')) { $main }
    })
    Invoke-SctUiButton $View 'すべて展開'
    [void](Wait-SctUi "expanded unread row $Name" {
        $main = Get-SctMonitorWindow $Process
        if ($main -and (Get-SctUiLabel $main "$Name を既読にする")) { $main }
    })
    Invoke-SctUiButton $View 'SCT-Watch を既読にする'
    [void](Wait-SctUi 'recursion fixture updates acknowledged' {
        $main = Get-SctMonitorWindow $Process
        if ($main -and (Get-SctUiLabel $main '未読 0 件')) { $main }
    })
}
