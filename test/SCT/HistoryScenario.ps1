function Test-SctHistory($Process,$View,[string]$Root,[string]$Evidence) {
    $beta = Join-Path $Root 'Beta-report.txt'
    $alpha = Join-Path $Root 'Alpha-report.txt'
    $other = Join-Path $Root 'Other.txt'
    $count = 0
    # Insert in non-alphabetic order in distinct, observed scans.
    foreach ($file in $beta,$other,$alpha) {
        [IO.File]::WriteAllText($file,'history fixture')
        $count++
        Invoke-SctUiButton $View '↻ 更新'
        $View = Wait-SctUi "unread $count before history" {
            $main = Get-SctMonitorWindow $Process
            if ($main -and (Get-SctUiLabel $main "未読 $count 件")) { $main }
        }
    }
    Invoke-SctUiButton $View 'SCT-Watch を既読にする'
    [void](Wait-SctUi 'all history fixtures acknowledged' {
        $main = Get-SctMonitorWindow $Process
        if ($main -and (Get-SctUiLabel $main '未読 0 件')) { $main }
    })
    Invoke-SctUiButton $View '履歴'
    $history = Wait-SctUi 'history opened and retained all acknowledged paths' {
        $window = Get-SctOwnedWindow $Process.Id 'かわったよ | 変更履歴'
        if ($window -and (Get-SctUiLabel $window $other)) { $window }
    }
    Assert-SctHistoryOrder $history @($alpha,$other,$beta) 'default newest first'

    $scope = [Windows.Automation.TreeScope]::Descendants
    $condition = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::Edit)
    $search = $history.FindFirst($scope,$condition)
    $value = $null
    if (!$search -or !$search.TryGetCurrentPattern([Windows.Automation.ValuePattern]::Pattern,[ref]$value)) {
        throw 'History search field was not editable.'
    }
    $value.SetValue('  REPORT  ')
    Assert-SctHistoryOrder $history @($alpha,$beta) 'case-insensitive and trimmed search'
    Invoke-SctUiButton $history '検知日時 ▾'
    Assert-SctHistoryOrder $history @($beta,$alpha) 'ascending time after header click'
    Invoke-SctUiButton $history '検知日時 ▴'
    Assert-SctHistoryOrder $history @($alpha,$beta) 'descending time after second click'
    Invoke-SctUiButton $history 'ファイルパス'
    Assert-SctHistoryOrder $history @($alpha,$beta) 'ascending path'
    Invoke-SctUiButton $history 'ファイルパス ▴'
    Assert-SctHistoryOrder $history @($beta,$alpha) 'descending path'
    $value.SetValue('')
    Assert-SctHistoryOrder $history @($other,$beta,$alpha) 'clearing search restores the excluded row'
    [IO.File]::WriteAllText($Evidence,'PASS: acknowledge kept history; trimmed case-insensitive filtering; chronological and path sorting in both directions; clear restored all rows.')
}

function Assert-SctHistoryOrder($History,[string[]]$Expected,[string]$Description) {
    [void](Wait-SctUi $Description {
        $scope = [Windows.Automation.TreeScope]::Descendants
        $prop = [Windows.Automation.AutomationElement]::ControlTypeProperty
        $condition = [Windows.Automation.PropertyCondition]::new($prop,[Windows.Automation.ControlType]::ListItem)
        $actual = @($History.FindAll($scope,$condition) | ForEach-Object {
            $nameCondition = [Windows.Automation.PropertyCondition]::new(
                [Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::Text)
            $_.FindAll($scope,$nameCondition) | ForEach-Object { $_.Current.Name } |
                Where-Object { [IO.Path]::IsPathRooted($_) -and $_.EndsWith('.txt') }
        })
        $actual.Count -eq $Expected.Count -and ($actual -join '|') -ceq ($Expected -join '|')
    })
}
