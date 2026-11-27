# The actual packaged CLI creates a checkpoint for the actual packaged GUI to load.
function Invoke-SctStalePreview([string]$Package,[string]$Work) {
    Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
    $install = Join-Path $Work 'app'
    $source = Join-Path $Work 'source'
    $destination = Join-Path $Work 'saved'
    [void][IO.Directory]::CreateDirectory($source)
    [void][IO.Directory]::CreateDirectory($destination)
    [IO.Compression.ZipFile]::ExtractToDirectory($Package,$install)
    $file = Join-Path $source 'report.txt'
    $saved = Join-Path $destination 'report.txt'
    $sentinel = Join-Path $destination 'sentinel.txt'
    [IO.File]::WriteAllText($file,'version one')
    [IO.File]::WriteAllText($sentinel,'existing unrelated output')
    $sentinelHash = (Get-FileHash -LiteralPath $sentinel).Hash

    & (Join-Path $install 'Kawattayo-cli.exe') checkpoint create --name 'SCT-baseline' --root $source | Out-Null
    if ($LASTEXITCODE) { throw "The packaged CLI failed to create the fixture checkpoint ($LASTEXITCODE)." }
    [IO.File]::WriteAllText($file,'version two, visible in preview')
    $exe = Join-Path $install 'かわったよ.exe'
    $process = Start-Process -FilePath $exe -ArgumentList '--show-collector' -WindowStyle Hidden -WorkingDirectory $install -PassThru
    try {
        $view = Wait-SctUi 'collector loaded the checkpoint' {
            if ($process.HasExited) { throw "Collector exited early ($($process.ExitCode))." }
            $window = Get-SctOwnedWindow $process.Id 'かわったよ · 差分収集'
            if ($window -and (Get-SctUiLabel $window 'チェックポイント：1件')) { $window }
        }
        Select-SctCollectorPreview $view
        [IO.File]::WriteAllText($file,'version three, changes since preview must not be downloaded')
        $finalSource = (Get-FileHash -LiteralPath $file).Hash
        Submit-SctCollectorDestination $process $view $destination
        [void](Wait-SctUi 'stale preview rejected with a diagnostic' {
            $status = Get-SctUiLabel $view '保存 0件 ／ 未保存 1件' -Prefix
            if ($status -and $status.Current.Name.Contains('比較後に元ファイルが変化しました。再比較してください。')) { $true }
        })
        if ((Test-Path -LiteralPath $saved) -or (Get-FileHash -LiteralPath $file).Hash -ne $finalSource -or
             (Get-FileHash -LiteralPath $sentinel).Hash -ne $sentinelHash) {
            throw 'The stale preview created output or changed fixture files.'
        }
        Select-SctCollectorPreview $view
        Submit-SctCollectorDestination $process $view $destination
        [void](Wait-SctUi 'recompare permits copy' {
            $status = Get-SctUiLabel $view '保存 1件 ／ 未保存 0件' -Prefix
            if ($status -and (Test-Path -LiteralPath $saved -PathType Leaf)) { $true }
        })
        if ((Get-FileHash -LiteralPath $saved).Hash -ne $finalSource -or
             (Get-FileHash -LiteralPath $sentinel).Hash -ne $sentinelHash) { throw 'The retried copy has unexpected bytes.' }
        [IO.File]::WriteAllText((Join-Path $Work 'observations.txt'),
            'PASS stale GUI preview refused without side effects; a new comparison permitted copying the exact current file.')
    } finally {
        try {
            if (!$process.HasExited -and !($process.CloseMainWindow() -and $process.WaitForExit(5000))) {
                $process.Kill()
                [void]$process.WaitForExit(5000)
            }
        } finally { $process.Dispose() }
    }
}

function Select-SctCollectorPreview($View) {
    Invoke-SctUiButton $View '比較'
    [void](Wait-SctUi 'one eligible change in preview' {
        Get-SctUiLabel $View '差分・確認不能：1件（確認不能 0件）'
    })
    $label = Get-SctUiLabel $View '更新' -Prefix
    # Match the result text, never the similarly named switch-to-monitor button.
    if (!$label -or !$label.Current.Name.EndsWith('report.txt')) {
        $scope = [Windows.Automation.TreeScope]::Descendants
        $label = $View.FindAll($scope,[Windows.Automation.Condition]::TrueCondition) |
            Where-Object { ([string]$_.Current.Name).EndsWith('report.txt') } | Select-Object -First 1
    }
    if (!$label) { throw 'The preview did not show the expected report path.' }
    (Get-SctDownloadSelection $View $label.Current.Name).Select()
}

function Submit-SctCollectorDestination($Process,$View,[string]$Destination) {
    Invoke-SctUiButton $View '選択分をダウンロード…'
    $picker = Wait-SctUi 'collector output folder picker' { Get-SctOwnedWindow $Process.Id '保存先を選択' }
    Submit-SctFolderPicker $picker $Process.Id $Destination
}
