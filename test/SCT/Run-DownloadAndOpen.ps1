
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Package,
    [Parameter(Mandatory)][guid]$RunId,
    [switch]$PrepareOnly,
    [switch]$DedicatedSession
)
$ErrorActionPreference = 'Stop'
if ($PrepareOnly.IsPresent -eq $DedicatedSession.IsPresent -or $RunId -eq [guid]::Empty) { throw 'Choose one mode and a fresh run ID.' }
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$work = Join-Path (Join-Path $repo 'build') ('OPEN-GUI-007-' + $RunId.ToString('N'))
$sourceFolder = Join-Path $work 'source'
$outside = Join-Path $work 'local-copy'
$install = Join-Path $work 'product'
$viewerFolder = Join-Path $work 'viewer'
$viewer = Join-Path $viewerFolder 'SctTextViewer.exe'
$invocations = Join-Path $viewerFolder 'invocations.txt'
$logPath = Join-Path $work 'environment-result.json'
$zip = (Resolve-Path -LiteralPath $Package).ProviderPath
$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
$file = Join-Path $sourceFolder 'open-fixture.txt'
$saved = Join-Path $outside 'open-fixture.txt'

if ($PrepareOnly) {
    if ([IO.Directory]::Exists($work) -or [IO.File]::Exists($work)) { throw 'Use a new run ID.' }
    foreach ($dir in $sourceFolder,$outside,$viewerFolder) { [void][IO.Directory]::CreateDirectory($dir) }
    [IO.Compression.ZipFile]::ExtractToDirectory($zip,$install)
    foreach ($name in 'settings.json','settings.json.bak','cache.json','cache.sqlite3','cache.sqlite3-wal','cache.sqlite3-shm') {
        if ([IO.File]::Exists((Join-Path $install $name))) { throw "ZIP contains application state: $name." }
    }
    & powershell.exe -NoProfile -NonInteractive -File (Join-Path $PSScriptRoot 'Fixtures/BuildHookRecorder.ps1') -Output $viewer
    if ($LASTEXITCODE -or ![IO.File]::Exists($viewer)) { throw 'Failed to build the test-only viewer under the current PowerShell execution policy.' }
    [IO.File]::WriteAllText($file,'original test document content')
    $config = @{Version=1;KeepInTray=$false;LowServerLoad=$false;PollSeconds=5;WaitForStable=$false;
        Items=@(@{Id=[guid]::NewGuid().ToString();Path=$file;IsFolder=$false})}
    [IO.File]::WriteAllText((Join-Path $install 'settings.json'),($config | ConvertTo-Json -Depth 4))
    [IO.File]::WriteAllText($logPath,(@{id='SCT-GUI-007';outcome='PREPARED_ONLY';packageSha256=$hash} | ConvertTo-Json))
    Write-Host "Fixture ready. Under a throwaway Windows test account only, associate .txt with: $viewer"
    return
}
$prepared = Get-Content -Raw -LiteralPath $logPath | ConvertFrom-Json
if ($prepared.id -cne 'SCT-GUI-007' -or $prepared.outcome -cne 'PREPARED_ONLY' -or $prepared.packageSha256 -ne $hash) {
    throw 'Prepare a fresh fixture for this exact ZIP first.'
}
if (![Environment]::UserInteractive -or ![Diagnostics.Process]::GetCurrentProcess().SessionId) { throw 'An isolated interactive Windows session is required.' }
$mutex = $null
if ([Threading.Mutex]::TryOpenExisting('Local\ShareFileWatch-v1',[ref]$mutex)) {
    $mutex.Dispose()
    throw 'Kawattayo is already running; the test will not close it.'
}
Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class SctViewerAssociation {
    [DllImport("shlwapi.dll",CharSet=CharSet.Unicode)]
    private static extern int AssocQueryString(uint flags,uint type,string association,string extra,StringBuilder output,ref uint length);
    public static string ReadTxtOpenExecutable() {
        uint size=32768;
        var text=new StringBuilder((int)size);
        return AssocQueryString(0,2,".txt","open",text,ref size)==0 ? text.ToString() : null;
    }
}
"@
if ([SctViewerAssociation]::ReadTxtOpenExecutable() -ine $viewer) {
    throw 'The .txt viewer in this session is not the prepared recorder. Never change the real account association.'
}
if ([IO.File]::Exists($invocations) -or @(Get-ChildItem -LiteralPath $outside -Force).Count -ne 0) {
    throw 'The prepared viewer log or destination is not empty.'
}
. (Join-Path $PSScriptRoot 'UnreadScenario.ps1')
. (Join-Path $PSScriptRoot 'MonitorLifecycle.ps1')
. (Join-Path $PSScriptRoot 'PreferencePersistence.ps1')
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$p = $null
$result = @{id='SCT-GUI-007';outcome='FAIL';packageSha256=$hash;atUtc=[DateTimeOffset]::UtcNow.ToString('o');
    windows=[Environment]::OSVersion.VersionString; copySha256=$null; error=$null }
try {
    $p = Start-SctUnreadApp (Join-Path $install 'かわったよ.exe')
    $view = Wait-SctUi 'initial monitor query for download and open' {
        $window = Get-SctOwnedWindow $p.Id 'かわったよ v' -Prefix
        if ($window -and (Get-SctUiLabel $window '最終巡回 ' -Prefix)) { $window }
    }
    [IO.File]::WriteAllText($file,'modified test document with a different length')
    $sourceHash = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash
    Invoke-SctUiButton $view '↻ 更新'
    [void](Wait-SctUi 'fixture file becomes unread' { Get-SctUiLabel $view 'open-fixture.txt を既読にする' })
    Read-Host "Use the test row's download-and-open action, select only the fixture destination [$outside], and press Enter when the action finishes"
    $line = Wait-SctUi 'test viewer receives the local copied document' {
        if ([IO.File]::Exists($invocations)) { Get-Content -LiteralPath $invocations }
    }
    $records = @(Get-Content -LiteralPath $invocations)
    if ($records.Count -ne 1 -or $records[0].Contains('|')) { throw 'The viewer must receive exactly one file argument and one launch.' }
    $actual = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($records[0]))
    if ($actual -ine $saved -or $actual -ieq $file -or ![IO.File]::Exists($saved)) {
        throw 'The viewer was not passed the exact saved copy.'
    }
    $result.copySha256 = (Get-FileHash -LiteralPath $saved -Algorithm SHA256).Hash
    if ($result.copySha256 -ne $sourceHash -or (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $sourceHash) {
        throw 'The original and copied files do not have the expected identical contents.'
    }
    $result.outcome = 'PASS'
} catch {
    $result.error = $_.Exception.Message
} finally {
    if ($p) { Stop-SctPreferenceFixture $p }
    [IO.File]::WriteAllText($logPath,($result | ConvertTo-Json -Depth 4))
    Write-Host "Evidence: $logPath ($($result.outcome))"
}
if ($result.outcome -ne 'PASS') { throw $result.error }
