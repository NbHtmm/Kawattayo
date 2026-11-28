param([switch]$RequireAllPatterns)
. (Join-Path $PSScriptRoot 'PatternAudit.ps1')
. (Join-Path $PSScriptRoot 'BoundaryTrace.ps1')
. (Join-Path $PSScriptRoot '../SCT/CaseLedger.ps1')
. (Join-Path $PSScriptRoot 'FunctionTrace.ps1')
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$out = Join-Path $repo ('build/UT-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($out)
# 既存の動作確認と、1ファイル1UTの移行状況は別々に見えるようにする。
$groups = @(
    @{ source = 'src/Core'; tests = 'Core' },
    @{ source = 'src/Windows'; tests = 'Windows' },
    @{ source = 'avalonia'; tests = 'Avalonia' },
    @{ source = 'cli'; tests = 'Cli' }
)
$mappings = foreach ($group in $groups) {
    $base = [IO.Path]::GetFullPath((Join-Path $repo $group.source))
    foreach ($file in Get-ChildItem -LiteralPath $base -File -Recurse -Filter '*.cs') {
        $relative = $file.FullName.Substring($base.Length + 1)
        if ($relative -match '(^|[\\/])(obj|bin|SmokeTests)([\\/]|$)' -or $file.Name -like '*.g.cs') { continue }
        $expected = ('test/UT/' + $group.tests + '/' + $relative.Substring(0, $relative.Length - 3) + '.Tests.cs').Replace('\', '/')
        [pscustomobject]@{
            source = ($group.source + '/' + $relative).Replace('\', '/')
            expected_test = $expected
            status = $(if (Test-Path -LiteralPath (Join-Path $repo $expected)) { 'mapped' } else { 'missing_test_file' })
        }
    }
}
$mappings | Sort-Object source | Export-Csv -LiteralPath (Join-Path $out 'source-map.csv') -NoTypeInformation -Encoding UTF8
$mapped = @($mappings | Where-Object { $_.status -eq 'mapped' }).Count
Write-Host "Source/test files: $mapped/$(@($mappings).Count) mapped (not function coverage)."
Push-Location $repo
try {
    dotnet run --project test/tools/UtInventory -c Release -- (Join-Path $out 'source-map.csv') (Join-Path $out 'functions.csv') $repo
    if ($LASTEXITCODE -ne 0) { throw 'Failed to generate the source function inventory.' }
    dotnet test test/UT/Kawattayo.Core.Tests.csproj -c Release -p:RestoreLockedMode=true -p:UseSharedCompilation=false --settings test/UT/coverage.runsettings --results-directory $out --logger 'trx;LogFileName=result.trx' --collect 'XPlat Code Coverage'
    $status = $LASTEXITCODE
    $resultPath = Join-Path $out 'result.trx'
    if (Test-Path -LiteralPath $resultPath) {
        [xml]$trx = Get-Content -LiteralPath $resultPath -Raw
        $trace = @{
            Trx = $trx
            Inventory = Join-Path $out 'functions.csv'
            Mapping = Join-Path $PSScriptRoot 'function-assertions.csv'
            Destination = Join-Path $out 'function-trace.csv'
            BoundaryMapping = Join-Path $PSScriptRoot 'function-boundaries.csv'
            CaseLedger = Join-Path $PSScriptRoot '../SCT/CASES.md'
        }
        Write-UtFunctionTrace @trace
        Write-UtPatternAudit -Trx $trx -Inventory $trace.Inventory -PatternLedger (Join-Path $PSScriptRoot 'function-patterns.csv') -Destination (Join-Path $out 'function-pattern-audit.csv') -RequireAllPatterns:$RequireAllPatterns
    }
    if ($status -eq 0) {
        if (!$trx) { throw 'UT result TRX missing.' }
        $counters = $trx.SelectSingleNode("//*[local-name()='Counters']")
        if (!$counters -or [int]$counters.executed -lt 1) { throw 'No unit tests executed.' }
        if (!(Get-ChildItem -LiteralPath $out -Filter 'coverage.cobertura.xml' -Recurse)) { throw 'Coverage report missing.' }
        # VSTest also makes a nested TRX attachment copy. Inspect the direct collector output only.
        $coverageReports = @(Get-ChildItem -LiteralPath $out -Directory |
            ForEach-Object { Get-ChildItem -LiteralPath $_.FullName -File -Filter 'coverage.opencover.xml' })
        if ($coverageReports.Count -ne 1) { throw 'Expected one OpenCover report.' }
        [xml]$coverage = Get-Content -LiteralPath $coverageReports[0].FullName -Raw
        $modules = @($coverage.CoverageSession.Modules.Module | ForEach-Object { $_.ModuleName })
        $expectedModules = @('Kawattayo.Core', 'Kawattayo.Windows', 'Kawattayo-cli', 'Kawattayo.Avalonia')
        $missing = @($expectedModules | Where-Object { $_ -cnotin $modules })
        if ($missing.Count -gt 0) { throw ('Coverage modules missing: ' + ($missing -join ', ')) }
    }
    exit $status
} finally {
    Write-Host "UT reports: $out"
    Pop-Location
}
