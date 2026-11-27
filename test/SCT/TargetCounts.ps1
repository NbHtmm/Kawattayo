# Show measured scope size in the packaged registration table and sort by numeric count.
function Invoke-SctTargetCount([string]$Package,[string]$Work) {
    Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
    [void][IO.Directory]::CreateDirectory($Work)
    $install = Join-Path $Work 'app'
    [IO.Compression.ZipFile]::ExtractToDirectory($Package,$install)
    $exe = Join-Path $install 'かわったよ.exe'
    $targets = @()
    foreach ($spec in @(@{Name='Alpha'; Count=3},@{Name='Beta'; Count=2})) {
        $folder = Join-Path $Work $spec.Name
        [void][IO.Directory]::CreateDirectory($folder)
        foreach ($n in 1..$spec.Count) { [IO.File]::WriteAllText((Join-Path $folder "$n.txt"),'counted file') }
        $targets += @{Id=[Guid]::NewGuid().ToString();Path=$folder;DisplayName=$spec.Name;IsFolder=$true;Recursive=$true}
    }
    $file = Join-Path $Work 'Gamma.txt'
    [IO.File]::WriteAllText($file,'only one file')
    $targets += @{Id=[Guid]::NewGuid().ToString();Path=$file;DisplayName='Gamma';IsFolder=$false}
    $settings = @{Version=1;KeepInTray=$false;LowServerLoad=$false;PollSeconds=5;Items=$targets}
    [IO.File]::WriteAllText((Join-Path $install 'settings.json'),($settings | ConvertTo-Json -Depth 4))

    $p = Start-SctUnreadApp $exe
    try {
        $view = Wait-SctUi 'three targets after initial query' {
            $main = Get-SctMonitorWindow $p
            if ($main -and (Get-SctUiLabel $main '最終巡回 ' -Prefix)) { $main }
        }
        $settings = Open-SctPreferenceWindow $view $p
        Assert-SctTargetCountOrder $settings @('Alpha:3','Beta:2','Gamma:1') 'default name order with counted files'
        Invoke-SctUiButton $settings '対象ファイル数'
        Assert-SctTargetCountOrder $settings @('Gamma:1','Beta:2','Alpha:3') 'ascending counts'
        Invoke-SctUiButton $settings '対象ファイル数 ▴'
        Assert-SctTargetCountOrder $settings @('Alpha:3','Beta:2','Gamma:1') 'descending counts'

        foreach ($n in 3..5) { [IO.File]::WriteAllText((Join-Path $Work "Beta/$n.txt"),'new counted file') }
        Invoke-SctUiButton $view '↻ 更新'
        Assert-SctTargetCountOrder $settings @('Beta:5','Alpha:3','Gamma:1') 'updated counts retain descending sort'
    } finally { Stop-SctPreferenceFixture $p }
    [IO.File]::WriteAllText((Join-Path $Work 'observations.txt'),
        'PASS: registration counts 3,2,1 sorted in both directions; after the second folder grew to 5, it moved to the top without changing the selected sort.')
}

function Assert-SctTargetCountOrder($Settings,[string[]]$Expected,[string]$Description) {
    [void](Wait-SctUi $Description {
        $scope = [Windows.Automation.TreeScope]::Descendants
        $property = [Windows.Automation.AutomationElement]::ControlTypeProperty
        $itemType = [Windows.Automation.PropertyCondition]::new($property,[Windows.Automation.ControlType]::ListItem)
        $textType = [Windows.Automation.PropertyCondition]::new($property,[Windows.Automation.ControlType]::Text)
        $actual = @($Settings.FindAll($scope,$itemType) | ForEach-Object {
            $labels = @($_.FindAll($scope,$textType) | ForEach-Object { $_.Current.Name })
            $name = @($labels | Where-Object { $_ -cin 'Alpha','Beta','Gamma' })
            if ($name.Count -eq 1) {
                $count = @($labels | Where-Object { $_ -match '^\d+$' })
                if ($count.Count -eq 1) { "$($name[0]):$($count[0])" }
            }
        })
        $actual.Count -eq $Expected.Count -and ($actual -join '|') -ceq ($Expected -join '|')
    })
}
