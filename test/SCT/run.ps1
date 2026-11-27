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

function Test-GuiSessionAvailable {
    try {
        $mutex = $null
        if ([Threading.Mutex]::TryOpenExisting('Local\ShareFileWatch-v1', [ref]$mutex)) {
            $mutex.Dispose()
            return $false
        }
    } catch {
        return $false
    }
    return !(Get-Process 'かわったよ','Kawattayo.Avalonia' -ErrorAction SilentlyContinue)
}

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
    $env:KAWATTAYO_SCT_WORK = $out
    $env:KAWATTAYO_SCT_CLI = $cli
    $messagesPath = Join-Path $out 'cucumber.ndjson'
    $env:REQNROLL_FORMATTERS_MESSAGE = 'outputFilePath=' + $messagesPath
    $project = Join-Path $PSScriptRoot 'Kawattayo.Sct.csproj'
    & dotnet restore $project --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'SCT package restore failed.' }

    & dotnet test $project --no-restore -p:ReqnrollGenerationTaskFactory=AssemblyTaskFactory --filter 'Category=SCT-CLI-001' --results-directory $out --logger 'trx;LogFileName=sct.trx'
    $exitCode = $LASTEXITCODE
    $trxPath = Join-Path $out 'sct.trx'
    if (![IO.File]::Exists($trxPath) -or ![IO.File]::Exists($messagesPath)) {
        throw "SCT evidence is missing (dotnet test exit code: $exitCode)."
    }
    [xml]$trx = [IO.File]::ReadAllText($trxPath)
    $rows = @($trx.SelectNodes("//*[local-name()='UnitTestResult']"))
    if ($exitCode -ne 0 -or $rows.Count -ne 1 -or $rows[0].outcome -ne 'Passed') {
        throw 'The checkpoint scenario did not pass. See sct.trx and cucumber.ndjson.'
    }
}

try {
    Push-Location $repo
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
    $app = Join-Path $out 'product'
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::ExtractToDirectory($package, $app)
    $gui = Join-Path $app 'かわったよ.exe'
    $cli = Join-Path $app 'Kawattayo-cli.exe'
    if (!(Test-Path -LiteralPath $gui) -or !(Test-Path -LiteralPath $cli)) { throw 'Package is missing the GUI or CLI executable.' }

    Invoke-Case 'SCT-CLI-001' { Invoke-CheckpointScenario }

    # 排他制御を確認できないときは通常版に触れず、合格にも数えない。
    if (!(Test-GuiSessionAvailable)) {
        $message = 'A running instance or an unavailable desktop session prevents a safe GUI launch.'
        $results.Add(@{ id = 'SCT-GUI-001'; outcome = 'BLOCKED'; error = $message })
        Write-Host "BLOCKED SCT-GUI-001: $message"
    } else {
        Invoke-Case 'SCT-GUI-001' {
            $p = Start-Process -FilePath $gui -ArgumentList '--show-collector' -WorkingDirectory $app -WindowStyle Hidden -PassThru
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
    }
    if ($results.Where({ $_.outcome -ne 'PASS' }).Count -gt 0) { $status = 1 }
} catch {
    $results.Add(@{ id = 'SCT-SETUP'; outcome = 'FAIL'; error = $_.Exception.Message })
    Write-Host "FAIL SCT-SETUP: $($_.Exception.Message)"
    $status = 1
} finally {
    foreach ($result in $results) {
        $result.testCode = 'test/SCT/run.ps1'
        if ($result.id -eq 'SCT-CLI-001') {
            $result.specification = 'test/SCT/Features/Checkpoint.feature'
            $result.testCode = 'test/SCT/Steps/CheckpointSteps.cs'
            $result.evidence = @('sct.trx', 'cucumber.ndjson' | Where-Object { Test-Path -LiteralPath (Join-Path $out $_) })
        } elseif ($result.id -ne 'SCT-SETUP') {
            $result.specification = 'test/SCT/SPEC.md#' + $result.id.ToLowerInvariant()
        }
    }
    $report = @{
        schemaVersion = 1
        runnerRevision = $runnerRevision
        artifact = $artifact
        results = @($results.ToArray())
    }
    $report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $out 'result.json') -Encoding UTF8
    Write-Host "SCT reports: $out"
    Pop-Location
}
exit $status
