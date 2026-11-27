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
    dotnet test test/UT/Kawattayo.Core.Tests.csproj -c Release -p:RestoreLockedMode=true -p:UseSharedCompilation=false --settings test/UT/coverage.runsettings --results-directory $out --logger 'trx;LogFileName=result.trx' --collect 'XPlat Code Coverage'
    $status = $LASTEXITCODE
    if ($status -eq 0) {
        [xml]$trx = Get-Content -LiteralPath (Join-Path $out 'result.trx') -Raw
        $counters = $trx.SelectSingleNode("//*[local-name()='Counters']")
        if (!$counters -or [int]$counters.executed -lt 1) { throw 'No unit tests executed.' }
        if (!(Get-ChildItem -LiteralPath $out -Filter 'coverage.cobertura.xml' -Recurse)) { throw 'Coverage report missing.' }
    }
    exit $status
} finally {
    Write-Host "UT reports: $out"
    Pop-Location
}
