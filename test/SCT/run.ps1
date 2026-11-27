. (Join-Path $PSScriptRoot 'CaseLedger.ps1')
. (Join-Path $PSScriptRoot 'Evidence.ps1')
. (Join-Path $PSScriptRoot 'DesktopSetup.ps1')
. (Join-Path $PSScriptRoot 'UnreadScenario.ps1')
. (Join-Path $PSScriptRoot 'MonitorLifecycle.ps1')
. (Join-Path $PSScriptRoot 'SettingsScenario.ps1')
. (Join-Path $PSScriptRoot 'BulkDownload.ps1')
. (Join-Path $PSScriptRoot 'HistoryScenario.ps1')
. (Join-Path $PSScriptRoot 'RecursionScenario.ps1')
. (Join-Path $PSScriptRoot 'PreferencePersistence.ps1')
. (Join-Path $PSScriptRoot 'RemoveRegistration.ps1')
. (Join-Path $PSScriptRoot 'TargetCounts.ps1')
. (Join-Path $PSScriptRoot 'RecursionOverride.ps1')
. (Join-Path $PSScriptRoot 'NestedInbox.ps1')
. (Join-Path $PSScriptRoot 'ClearDeleted.ps1')
. (Join-Path $PSScriptRoot 'PickerRegistration.ps1')
. (Join-Path $PSScriptRoot 'HookScenario.ps1')
. (Join-Path $PSScriptRoot 'CollectorScenario.ps1')

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$out = Join-Path $repo ('build/SCT-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($out)
$package = $env:KAWATTAYO_SCT_PACKAGE
$results = [Collections.Generic.List[object]]::new()
$artifact = $null
$runnerRevision = @{ commit = $null; hasLocalChanges = $null }
$packageSource = if ($package) { 'provided' } else { 'built-in-this-run' }
$status = 0
$ledger = $null

function Invoke-Case([string]$id, [scriptblock]$test) {
    try {
        & $test
        $results.Add(@{ id = $id; outcome = 'PASS' })
        Write-Host "PASS $id"
    } catch {
        $results.Add(@{ id = $id; outcome = 'FAIL'; error = $_.Exception.Message })
        Write-Host "FAIL ${id}: $($_.Exception.Message)"
    }
}

function Invoke-CheckpointScenario {
    param([string]$CaseId)
    $caseRoot = Join-Path $out $CaseId
    [void][IO.Directory]::CreateDirectory($caseRoot)
    $install = Join-Path $caseRoot 'product'
    # Each scenario gets an independent SQLite database and unchanged product executable.
    [IO.Compression.ZipFile]::ExtractToDirectory($package, $install)
    $env:KAWATTAYO_SCT_WORK = $caseRoot
    $env:KAWATTAYO_SCT_CLI = Join-Path $install 'Kawattayo-cli.exe'
    $messagesPath = Join-Path $caseRoot 'cucumber.ndjson'
    $env:REQNROLL_FORMATTERS_MESSAGE = 'outputFilePath=' + $messagesPath
    $project = Join-Path $PSScriptRoot 'Kawattayo.Sct.csproj'
    & dotnet restore $project --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'SCT package restore failed.' }

    & dotnet test $project --no-restore -p:ReqnrollGenerationTaskFactory=AssemblyTaskFactory --filter "Category=$CaseId" --settings (Join-Path $PSScriptRoot 'strict.runsettings') --results-directory $caseRoot --logger 'trx;LogFileName=sct.trx'
    $exitCode = $LASTEXITCODE
    $trxPath = Join-Path $caseRoot 'sct.trx'
    if (![IO.File]::Exists($trxPath) -or ![IO.File]::Exists($messagesPath)) {
        throw "SCT evidence is missing (dotnet test exit code: $exitCode)."
    }
    [xml]$trx = [IO.File]::ReadAllText($trxPath)
    $rows = @($trx.SelectNodes("//*[local-name()='UnitTestResult']"))
    if ($exitCode -ne 0 -or $rows.Count -ne 1 -or $rows[0].outcome -ne 'Passed') {
        throw 'The checkpoint scenario did not pass. See sct.trx and cucumber.ndjson.'
    }
    Assert-SctScenarioEvidence -Path $messagesPath -CaseId $CaseId
}

try {
    Push-Location $repo
    $ledger = Read-SctCaseLedger (Join-Path $PSScriptRoot 'CASES.md')
    # 提供されたZIPには、試験コードのコミットを製品のコミットとして帰属させない。
    if (Get-Command git -ErrorAction SilentlyContinue) {
        $revision = git rev-parse --verify HEAD 2>$null
        if ($LASTEXITCODE -eq 0) {
            $runnerRevision.commit = [string]$revision
            $changes = @(git status --porcelain 2>$null)
            if ($LASTEXITCODE -eq 0) { $runnerRevision.hasLocalChanges = $changes.Count -gt 0 }
        }
    }
    if (!$package) {
        $package = Join-Path $out 'Kawattayo-test.zip'
        & (Join-Path $repo 'tool/package-avalonia.ps1') -OutputPath $package
    }
    $package = (Resolve-Path -LiteralPath $package).ProviderPath
    $hasher = [Security.Cryptography.SHA256]::Create()
    $stream = [IO.File]::OpenRead($package)
    try { $hash = ([BitConverter]::ToString($hasher.ComputeHash($stream))).Replace('-', '') }
    finally { $stream.Dispose(); $hasher.Dispose() }
    $artifact = @{ path = $package; sha256 = $hash; source = $packageSource }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($package)
    try {
        $entryNames = @($zip.Entries.FullName)
        if ('かわったよ.exe' -cnotin $entryNames -or 'Kawattayo-cli.exe' -cnotin $entryNames) {
            throw 'Package is missing the GUI or CLI executable.'
        }
        $stateNames = @('settings.json','settings.json.bak','cache.json','cache.sqlite3','cache.sqlite3-wal','cache.sqlite3-shm')
        if (@($entryNames | Where-Object { $_ -in $stateNames }).Count -gt 0) {
            throw 'The package contains application state and cannot be used as a clean fixture.'
        }
    } finally { $zip.Dispose() }

    $desktop = Join-Path $out 'desktop'
    $desktopReady = $false
    try {
        Initialize-SctDesktop -Package $package -Directory $desktop
        $desktopReady = $true
    } catch {
        $message = $_.Exception.Message
        foreach ($id in 'SCT-GUI-001','SCT-GUI-002','SCT-GUI-004','SCT-GUI-005','SCT-GUI-006','SCT-GUI-008','SCT-GUI-009','SCT-GUI-010','SCT-GUI-011','SCT-GUI-012','SCT-GUI-013','SCT-GUI-014','SCT-GUI-016','SCT-GUI-017','SCT-GUI-018','SCT-GUI-019','SCT-GUI-020','SCT-GUI-021','SCT-GUI-022','SCT-GUI-023','SCT-PERSIST-001','SCT-PERSIST-002','SCT-PERSIST-003','SCT-PERSIST-004','SCT-HOOK-001','SCT-HOOK-002','SCT-HOOK-003') {
            $results.Add(@{ id = $id; outcome = 'BLOCKED'; error = $message })
            Write-Host "BLOCKED ${id}: $message"
        }
    }
    foreach ($id in 'SCT-CLI-001','SCT-CLI-002','SCT-CLI-003','SCT-CLI-004','SCT-CLI-005','SCT-CLI-006','SCT-CLI-008','SCT-CLI-009','SCT-CLI-010','SCT-CLI-011','SCT-CLI-013') {
        Invoke-Case $id { Invoke-CheckpointScenario -CaseId $id }
    }

    $volume = [IO.DriveInfo]::new([IO.Path]::GetPathRoot($out))
    foreach ($ntfsCase in 'SCT-CLI-007','SCT-CLI-012','SCT-CLI-014') {
        if ($volume.DriveFormat -eq 'NTFS') {
            Invoke-Case $ntfsCase { Invoke-CheckpointScenario -CaseId $ntfsCase }
        } else {
            $results.Add(@{ id = $ntfsCase; outcome = 'BLOCKED'; error = 'This fixture requires NTFS.' })
            Write-Host "BLOCKED ${ntfsCase}: This fixture requires NTFS."
        }
    }

    if ($desktopReady) {
        $gui = Join-Path $desktop 'かわったよ.exe'
        Invoke-Case 'SCT-GUI-001' {
            $p = Start-Process -FilePath $gui -ArgumentList '--show-collector' -WorkingDirectory $desktop -WindowStyle Hidden -PassThru
            try {
                $deadline = [Diagnostics.Stopwatch]::StartNew()
                while ($deadline.Elapsed.TotalSeconds -lt 15) {
                    $p.Refresh()
                    if ($p.HasExited) { throw "GUI exited before showing the collector ($($p.ExitCode))." }
                    if ($p.MainWindowTitle -like '*差分収集*') { return }
                    Start-Sleep -Milliseconds 100
                }
                throw 'The collector window did not appear.'
            } finally {
                if (!$p.HasExited) { $p.Kill(); [void]$p.WaitForExit(5000) }
                $p.Dispose()
            }
        }
        Invoke-Case 'SCT-GUI-002' { Invoke-SctUnreadScenario -Package $package -Work (Join-Path $out 'unread') }
        Invoke-Case 'SCT-GUI-004' { Invoke-SctMonitorLifecycle $package (Join-Path $out 'SCT-GUI-004') 'FileLifecycle' }
        Invoke-Case 'SCT-GUI-005' { Invoke-SctMonitorLifecycle $package (Join-Path $out 'SCT-GUI-005') 'FolderOffline' }
        Invoke-Case 'SCT-GUI-006' { Invoke-SctUnreadScenario -Package $package -Work (Join-Path $out 'SCT-GUI-006') -BulkDownload }
        Invoke-Case 'SCT-GUI-008' { Invoke-SctStalePreview $package (Join-Path $out 'SCT-GUI-008') }
        Invoke-Case 'SCT-GUI-009' { Invoke-SctPickerRegistration $package (Join-Path $out 'SCT-GUI-009') }
        Invoke-Case 'SCT-GUI-010' { Invoke-SctMonitorLifecycle $package (Join-Path $out 'SCT-GUI-010') 'Recursion' }
        Invoke-Case 'SCT-GUI-011' { Invoke-SctMonitorLifecycle $package (Join-Path $out 'SCT-GUI-011') 'Exclusions' }
        Invoke-Case 'SCT-GUI-012' { Invoke-SctMonitorLifecycle $package (Join-Path $out 'SCT-GUI-012') 'History' }
        Invoke-Case 'SCT-GUI-013' { Invoke-SctExistingInstance $package (Join-Path $out 'SCT-GUI-013') }
        Invoke-Case 'SCT-GUI-014' { Invoke-SctPreferencePersistence $package (Join-Path $out 'SCT-GUI-014') }
        Invoke-Case 'SCT-GUI-016' { Invoke-SctMonitorLifecycle $package (Join-Path $out 'SCT-GUI-016') 'PauseResume' }
        Invoke-Case 'SCT-GUI-017' { Invoke-SctMonitorLifecycle $package (Join-Path $out 'SCT-GUI-017') 'LowLoadWatcher' }
        Invoke-Case 'SCT-GUI-018' { Invoke-SctRemoveRegistration $package (Join-Path $out 'SCT-GUI-018') }
        Invoke-Case 'SCT-GUI-019' { Invoke-SctTargetCount $package (Join-Path $out 'SCT-GUI-019') }
        Invoke-Case 'SCT-GUI-020' { Invoke-SctRecursionOverride $package (Join-Path $out 'SCT-GUI-020') }
        Invoke-Case 'SCT-GUI-021' { Invoke-SctNestedInbox $package (Join-Path $out 'SCT-GUI-021') }
        Invoke-Case 'SCT-GUI-023' { Invoke-SctCollectorCreation $package (Join-Path $out 'SCT-GUI-023') }
        Invoke-Case 'SCT-GUI-022' { Invoke-SctClearDeleted $package (Join-Path $out 'SCT-GUI-022') }
        Invoke-Case 'SCT-HOOK-001' { Invoke-SctUpdateHook $package (Join-Path $out 'SCT-HOOK-001') }
        Invoke-Case 'SCT-HOOK-002' { Invoke-SctUpdateHook $package (Join-Path $out 'SCT-HOOK-002') -Negative }
        Invoke-Case 'SCT-HOOK-003' { Invoke-SctUpdateHook $package (Join-Path $out 'SCT-HOOK-003') -RemovedWhileWaiting }
        Invoke-Case 'SCT-PERSIST-001' { Invoke-SctSettingsRecovery $package (Join-Path $out 'SCT-PERSIST-001') }
        Invoke-Case 'SCT-PERSIST-002' { Invoke-SctDatabaseRecovery $package (Join-Path $out 'SCT-PERSIST-002') }
        Invoke-Case 'SCT-PERSIST-004' { Invoke-SctSettingsSchemaRejection $package (Join-Path $out 'SCT-PERSIST-004') }
        Invoke-Case 'SCT-PERSIST-003' { Invoke-SctUnreadScenario -Package $package -Work (Join-Path $out 'SCT-PERSIST-003') -DatabaseWriteOutage }
    }
    Assert-SctRunMatchesLedger $ledger $results
    if ($results.Where({ $_.outcome -ne 'PASS' }).Count -gt 0) { $status = 1 }
} catch {
    $results.Add(@{ id = 'SCT-SETUP'; outcome = 'FAIL'; error = $_.Exception.Message })
    Write-Host "FAIL SCT-SETUP: $($_.Exception.Message)"
    $status = 1
} finally {
    foreach ($result in $results) {
        $result.testCode = 'test/SCT/run.ps1'
        if ($result.id -like 'SCT-CLI-*') {
            $caseId = $result.id
            $result.specification = 'test/SCT/Features/Checkpoint.feature'
            $result.testCode = 'test/SCT/Steps/CheckpointSteps.cs'
            if ($caseId -ne 'SCT-CLI-001') {
                $result.specification = 'test/SCT/Features/CheckpointSafety.feature'
                $result.testCode = 'test/SCT/Steps/CheckpointSafetySteps.cs'
            }
            if ($caseId -eq 'SCT-CLI-007') { $result.testCode = 'test/SCT/Steps/CheckpointJunctionSteps.cs' }
            if ($caseId -eq 'SCT-CLI-011') { $result.testCode = 'test/SCT/Steps/CheckpointVolumeSteps.cs' }
            if ($caseId -eq 'SCT-CLI-012') { $result.testCode = 'test/SCT/Steps/CheckpointZoneSteps.cs' }
            if ($caseId -eq 'SCT-CLI-013') { $result.testCode = 'test/SCT/Steps/CheckpointLockedSourceSteps.cs' }
            if ($caseId -eq 'SCT-CLI-014') { $result.testCode = 'test/SCT/Steps/CheckpointPathCaseSteps.cs' }
            $result.evidence = @('sct.trx', 'cucumber.ndjson' |
                ForEach-Object { "$caseId/$_" } | Where-Object { Test-Path -LiteralPath (Join-Path $out $_) })
        } elseif ($result.id -ne 'SCT-SETUP') {
            if ($result.id -eq 'SCT-GUI-002') {
                $result.testCode = 'test/SCT/UnreadScenario.ps1'
                $result.evidence = @('unread/observations.txt', 'unread/ui-on-failure.txt' |
                    Where-Object { Test-Path -LiteralPath (Join-Path $out $_) })
            }
            if ($result.id -eq 'SCT-GUI-006') {
                $result.testCode = 'test/SCT/BulkDownload.ps1'
                $result.evidence = @('SCT-GUI-006/observations.txt','SCT-GUI-006/ui-on-failure.txt' |
                    Where-Object { Test-Path -LiteralPath (Join-Path $out $_) })
            }
            if ($result.id -in 'SCT-GUI-004','SCT-GUI-005','SCT-GUI-011','SCT-GUI-013','SCT-GUI-016','SCT-GUI-017') {
                $result.testCode = 'test/SCT/MonitorLifecycle.ps1'
                $log = "$($result.id)/observations.txt"
                if (Test-Path -LiteralPath (Join-Path $out $log)) { $result.evidence = @($log) }
            }
            if ($result.id -eq 'SCT-GUI-008') {
                $result.testCode = 'test/SCT/CollectorScenario.ps1'
                $log = 'SCT-GUI-008/observations.txt'
                if (Test-Path -LiteralPath (Join-Path $out $log)) { $result.evidence = @($log) }
            }
            if ($result.id -eq 'SCT-GUI-009') {
                $result.testCode = 'test/SCT/PickerRegistration.ps1'
                $log = 'SCT-GUI-009/observations.txt'
                if (Test-Path -LiteralPath (Join-Path $out $log)) { $result.evidence = @($log) }
            }
            if ($result.id -eq 'SCT-GUI-010') {
                $result.testCode = 'test/SCT/RecursionScenario.ps1'
                $log = 'SCT-GUI-010/observations.txt'
                if (Test-Path -LiteralPath (Join-Path $out $log)) { $result.evidence = @($log) }
            }
            if ($result.id -eq 'SCT-GUI-012') {
                $result.testCode = 'test/SCT/HistoryScenario.ps1'
                $log = 'SCT-GUI-012/observations.txt'
                if (Test-Path -LiteralPath (Join-Path $out $log)) { $result.evidence = @($log) }
            }
            if ($result.id -eq 'SCT-GUI-014') {
                $result.testCode = 'test/SCT/PreferencePersistence.ps1'
                $log = 'SCT-GUI-014/observations.txt'
                if (Test-Path -LiteralPath (Join-Path $out $log)) { $result.evidence = @($log) }
            }
            if ($result.id -eq 'SCT-GUI-018') {
                $result.testCode = 'test/SCT/RemoveRegistration.ps1'
                $log = 'SCT-GUI-018/observations.txt'
                if (Test-Path -LiteralPath (Join-Path $out $log)) { $result.evidence = @($log) }
            }
            if ($result.id -eq 'SCT-GUI-019') {
                $result.testCode = 'test/SCT/TargetCounts.ps1'
                $log = 'SCT-GUI-019/observations.txt'
                if (Test-Path -LiteralPath (Join-Path $out $log)) { $result.evidence = @($log) }
            }
            if ($result.id -eq 'SCT-GUI-020') {
                $result.testCode = 'test/SCT/RecursionOverride.ps1'
                $log = 'SCT-GUI-020/observations.txt'
                if (Test-Path -LiteralPath (Join-Path $out $log)) { $result.evidence = @($log) }
            }
            if ($result.id -eq 'SCT-GUI-021') {
                $result.testCode = 'test/SCT/NestedInbox.ps1'
                $log = 'SCT-GUI-021/observations.txt'
                if (Test-Path -LiteralPath (Join-Path $out $log)) { $result.evidence = @($log) }
            }
            if ($result.id -eq 'SCT-GUI-023') {
                $result.testCode = 'test/SCT/CollectorScenario.ps1'
                $log = 'SCT-GUI-023/observations.txt'
                if (Test-Path -LiteralPath (Join-Path $out $log)) { $result.evidence = @($log) }
            }
            if ($result.id -eq 'SCT-GUI-022') {
                $result.testCode = 'test/SCT/ClearDeleted.ps1'
                $log = 'SCT-GUI-022/observations.txt'
                if (Test-Path -LiteralPath (Join-Path $out $log)) { $result.evidence = @($log) }
            }
            if ($result.id -eq 'SCT-PERSIST-004') {
                $result.testCode = 'test/SCT/SettingsScenario.ps1'
                $log = 'SCT-PERSIST-004/observations.txt'
                if (Test-Path -LiteralPath (Join-Path $out $log)) { $result.evidence = @($log) }
            }
            if ($result.id -in 'SCT-HOOK-001','SCT-HOOK-002','SCT-HOOK-003') {
                $result.testCode = 'test/SCT/HookScenario.ps1'
                $result.evidence = @("$($result.id)/observations.txt","$($result.id)/invocations.txt" |
                    Where-Object { Test-Path -LiteralPath (Join-Path $out $_) })
            }
            if ($result.id -in 'SCT-PERSIST-001','SCT-PERSIST-002','SCT-PERSIST-003') {
                $result.testCode = 'test/SCT/SettingsScenario.ps1'
                if ($result.id -eq 'SCT-PERSIST-003') { $result.testCode = 'test/SCT/UnreadScenario.ps1' }
                $log = "$($result.id)/observations.txt"
                if (Test-Path -LiteralPath (Join-Path $out $log)) { $result.evidence = @($log) }
            }
            $result.specification = 'test/SCT/SPEC.md#' + $result.id.ToLowerInvariant()
        }
    }
    $report = @{
        schemaVersion = 1
        runnerRevision = $runnerRevision
        artifact = $artifact
        planned = @($ledger.Keys | Where-Object { $ledger[$_] -eq '予定' } | Sort-Object)
        environmentRequired = @($ledger.Keys | Where-Object { $ledger[$_] -eq '要環境' } | Sort-Object)
        results = @($results.ToArray())
    }
    $report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $out 'result.json') -Encoding UTF8
    Write-Host "SCT reports: $out"
    Pop-Location
}
exit $status
