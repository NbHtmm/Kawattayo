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

function Invoke-ProductCli([string]$label, [string]$arguments) {
    $stdout = Join-Path $out ($label + '.stdout.txt')
    $stderr = Join-Path $out ($label + '.stderr.txt')
    $info = New-Object Diagnostics.ProcessStartInfo
    $info.FileName = $cli
    $info.Arguments = $arguments
    $info.WorkingDirectory = $out
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.StandardOutputEncoding = [Text.Encoding]::UTF8
    $info.StandardErrorEncoding = [Text.Encoding]::UTF8
    $p = [Diagnostics.Process]::Start($info)
    try {
        $readOut = $p.StandardOutput.ReadToEndAsync()
        $readErr = $p.StandardError.ReadToEndAsync()
        if (!$p.WaitForExit(20000)) { throw "CLI timeout: $label" }
        $content = $readOut.GetAwaiter().GetResult()
        $errors = $readErr.GetAwaiter().GetResult()
        [IO.File]::WriteAllText($stdout, $content)
        [IO.File]::WriteAllText($stderr, $errors)
        return @{ exitCode = $p.ExitCode; output = $content.Trim(); error = $errors.Trim() }
    } finally {
        if (!$p.HasExited) { $p.Kill() }
        $p.Dispose()
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

    Invoke-Case 'SCT-CLI-001' {
        $root = Join-Path $out '監視 フォルダ'
        $destination = Join-Path $out '保存 フォルダ'
        [void][IO.Directory]::CreateDirectory($root)
        [void][IO.Directory]::CreateDirectory($destination)
        $sample = Join-Path $root '文書 01.txt'
        [IO.File]::WriteAllText($sample, 'before')
        $create = Invoke-ProductCli 'cli-create' ('checkpoint create --name baseline --root "' + $root + '"')
        if ($create.exitCode -ne 0 -or $create.output -notmatch '^[a-f0-9]{32}$') { throw "Invalid checkpoint result: $($create.error)" }
        [IO.File]::WriteAllText($sample, 'updated, longer content')
        $diff = Invoke-ProductCli 'cli-diff' ('diff --checkpoint ' + $create.output + ' --json')
        $rows = @($diff.output | ConvertFrom-Json)
        if ($diff.exitCode -ne 0 -or $rows.Count -ne 1 -or $rows[0].kind -ne 'updated' -or $rows[0].path -ne '文書 01.txt') {
            throw "Unexpected diff: $($diff.output)"
        }
        $collect = 'collect --checkpoint ' + $create.output + ' --output "' + $destination + '" --json'
        $saved = Invoke-ProductCli 'cli-collect' $collect
        $copy = Join-Path $destination '文書 01.txt'
        if ($saved.exitCode -ne 0 -or ![IO.File]::Exists($copy) -or [IO.File]::ReadAllText($copy) -ne 'updated, longer content') {
            throw 'Unexpected collected file.'
        }
        $again = Invoke-ProductCli 'cli-collect-existing' $collect
        if ($again.exitCode -ne 3 -or [IO.File]::ReadAllText($copy) -ne 'updated, longer content') {
            throw 'An existing destination must not be overwritten.'
        }
    }

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
        if ($result.id -ne 'SCT-SETUP') {
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
