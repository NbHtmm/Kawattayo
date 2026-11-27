[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Package
)
$ErrorActionPreference = 'Stop'
$zip = (Resolve-Path -LiteralPath $Package).ProviderPath
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$build = Join-Path $repo 'build'
$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
$scripts = @(Get-ChildItem -LiteralPath $PSScriptRoot -Filter 'Run-*.ps1' -File)
if ($scripts.Count -lt 9) { throw 'Expected all dedicated-environment scripts to be present.' }
foreach ($script in $scripts) {
    $tokens = $null
    $errors = $null
    [void][Management.Automation.Language.Parser]::ParseFile($script.FullName,[ref]$tokens,[ref]$errors)
    if ($errors) { throw "Parse error in $($script.Name): $errors" }
}

# These entry points only prepare fixtures. A .invalid UNC name is deliberately never accessed.
$longParts = (1..5 | ForEach-Object { ('s' * 46) + $_ }) -join '\'
$modes = @(
    @{Script='Run-NotificationActivation.ps1'; Case='SCT-GUI-003'; Prefix='NOTIFY-GUI-003-'; Args=@{}},
    @{Script='Run-DownloadAndOpen.ps1'; Case='SCT-GUI-007'; Prefix='OPEN-GUI-007-'; Args=@{RunId=[guid]::NewGuid()}},
    @{Script='Run-ExplorerDrop.ps1'; Case='SCT-GUI-015'; Prefix='EXPLORER-GUI-015-'; Args=@{}},
    @{Script='Run-NotificationTiming.ps1'; Case='SCT-NOTIFY-001'; Prefix='NOTIFY-001-'; Args=@{}},
    @{Script='Run-SmbReconnect.ps1'; Case='SCT-SMB-001'; Prefix='SMB-001-';
        Args=@{FixtureRoot='\\test-server.invalid\sct-share\Kawattayo-Sct\Disconnect'}},
    @{Script='Run-SmbPathChecks.ps1'; Case='SCT-SMB-003'; Prefix='SMB-003-';
        Args=@{FixtureRoot='\\test-server.invalid\sct-share\Kawattayo-Sct\Paths';
            LongRelativePath="readable\$longParts\measurement.txt"}}
)
foreach ($mode in $modes) {
    $known = @(Get-ChildItem -LiteralPath $build -Directory -Filter ($mode.Prefix+'*') -ErrorAction SilentlyContinue |
        Select-Object -ExpandProperty Name)
    $extra = $mode.Args
    & (Join-Path $PSScriptRoot $mode.Script) -Package $zip -PrepareOnly @extra
    $created = @(Get-ChildItem -LiteralPath $build -Directory -Filter ($mode.Prefix+'*') |
        Where-Object { $_.Name -cnotin $known })
    if ($created.Count -ne 1) { throw "Expected one new fixture for $($mode.Case)." }
    $record = Get-Content -LiteralPath (Join-Path $created[0].FullName 'environment-result.json') -Raw | ConvertFrom-Json
    if ($record.id -cne $mode.Case -or $record.outcome -cne 'PREPARED_ONLY' -or $record.packageSha256 -cne $hash) {
        throw "Untrusted or mislabeled preparation evidence: $($mode.Case)."
    }
}
Write-Host "Prepared-only evidence verified for $($modes.Count) case drivers; parsed $($scripts.Count) dedicated scripts."
Write-Host 'This does not run or pass real dedicated SCT cases.'
