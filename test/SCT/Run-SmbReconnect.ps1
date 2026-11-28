[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Package,
    [Parameter(Mandatory)][string]$FixtureRoot,
    [switch]$PrepareOnly,
    [switch]$DedicatedShare
)
$ErrorActionPreference = 'Stop'
if ($PrepareOnly.IsPresent -eq $DedicatedShare.IsPresent) { throw 'Choose exactly one run mode.' }
if ($FixtureRoot -notmatch '^\\\\[^\\]+\\[^\\]+\\Kawattayo-Sct\\Disconnect\\?$') {
    throw 'Specify the exact dedicated UNC fixture named Kawattayo-Sct\Disconnect.'
}
$root = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($FixtureRoot))
$name = 'disconnect-fixture.txt'
$file = [IO.Path]::Combine($root,$name)
$zip = (Resolve-Path -LiteralPath $Package).ProviderPath
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$work = Join-Path (Join-Path $repo 'build') ('SMB-001-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($work)
$path = Join-Path $work 'environment-result.json'
$log = @{id='SCT-SMB-001';outcome='PREPARED_ONLY';packageSha256=(Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
    windows=[Environment]::OSVersion.VersionString;atUtc=[DateTimeOffset]::UtcNow.ToString('o');offlineObserved=$false
    baseline=$null; afterReconnect=$null; error=$null}
if ($PrepareOnly) {
    [IO.File]::WriteAllText($path,($log | ConvertTo-Json -Depth 4))
    Write-Host "Fixture contract recorded without accessing the UNC share: $path"
    return
}
$log.outcome = 'BLOCKED'
$p = $null
try {
    if (![Environment]::UserInteractive -or ![Diagnostics.Process]::GetCurrentProcess().SessionId) { throw 'Use an isolated interactive Windows desktop.' }
    $mutex = $null
    if ([Threading.Mutex]::TryOpenExisting('Local\ShareFileWatch-v1',[ref]$mutex)) {
        $mutex.Dispose()
        throw 'Kawattayo is already running; the test will not close it.'
    }
    $listed = @([IO.Directory]::EnumerateFileSystemEntries($root))
    if ($listed.Count -ne 1 -or $listed[0] -ine $file) { throw 'The dedicated folder must contain just disconnect-fixture.txt.' }
    $before = Get-Item -LiteralPath $file
    if ($before.PSIsContainer -or ($before.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'Use an ordinary test-only document.'
    }
    $log.baseline = @{size=$before.Length; modifiedUtc=$before.LastWriteTimeUtc.ToString('o')}
    $log.outcome = 'FAIL'
    . (Join-Path $PSScriptRoot 'UnreadScenario.ps1')
    . (Join-Path $PSScriptRoot 'MonitorLifecycle.ps1')
    . (Join-Path $PSScriptRoot 'PreferencePersistence.ps1')
    Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
    $install = Join-Path $work 'product'
    [IO.Compression.ZipFile]::ExtractToDirectory($zip,$install)
    foreach ($n in 'settings.json','settings.json.bak','cache.json','cache.sqlite3','cache.sqlite3-wal','cache.sqlite3-shm') {
        if ([IO.File]::Exists((Join-Path $install $n))) { throw "ZIP contains application state: $n." }
    }
    $config = @{Version=1;KeepInTray=$false;LowServerLoad=$false;PollSeconds=5;NotifyDeleted=$true;WaitForStable=$false;
        Items=@(@{Id=[guid]::NewGuid().ToString();Path=$root;IsFolder=$true;Recursive=$false})}
    [IO.File]::WriteAllText((Join-Path $install 'settings.json'),($config | ConvertTo-Json -Depth 4))
    $p = Start-SctUnreadApp (Join-Path $install 'かわったよ.exe')
    $main = Wait-SctUi 'dedicated share initially available' {
        $w = Get-SctOwnedWindow $p.Id 'かわったよ v' -Prefix
        if ($w -and (Get-SctUiLabel $w '最終巡回 ' -Prefix) -and (Get-SctUiLabel $w '未読 0 件') -and
            !(Get-SctUiLabel $w '監視失敗 1件' -Prefix)) { $w }
    }
    Read-Host 'In the isolated lab, disconnect only A from the test share. Leave client B connected. Press Enter on A when done'
    Invoke-SctUiButton $main '↻ 更新'
    [void](Wait-SctUi 'offline share is a failure, not a deletion' {
        (Get-SctUiLabel $main '監視失敗 1件' -Prefix) -and (Get-SctUiLabel $main '未読 0 件')
    })
    $log.offlineObserved = $true
    Read-Host 'On the still-connected client B, change the length of disconnect-fixture.txt, leave its name intact, then reconnect A and press Enter'
    $after = Get-Item -LiteralPath $file
    if ($after.Length -eq $before.Length) { throw 'The controlled update did not change length.' }
    $log.afterReconnect = @{size=$after.Length; modifiedUtc=$after.LastWriteTimeUtc.ToString('o')}
    Invoke-SctUiButton $main '↻ 更新'
    [void](Wait-SctUi 'reconnected share has exactly one update' {
        (Get-SctUiLabel $main '未読 1 件') -and !(Get-SctUiLabel $main '監視失敗 1件' -Prefix)
    })
    Invoke-SctUiButton $main 'すべて展開'
    [void](Wait-SctUi 'updated fixture document in unread inbox' {
        (Get-SctUiLabel $main "$name を既読にする") -and (Get-SctUiLabel $main '更新') -and !(Get-SctUiLabel $main '消失')
    })
    Invoke-SctUiButton $main '履歴'
    $history = Wait-SctUi 'the exact fixture document in history' {
        $w = Get-SctOwnedWindow $p.Id 'かわったよ | 変更履歴'
        if ($w -and (Get-SctUiLabel $w $file)) { $w }
    }
    if (Get-SctUiLabel $history '消失') { throw 'History interpreted the offline interval as a disappearance.' }
    $log.outcome = 'PASS'
} catch {
    $log.error = $_.Exception.Message
} finally {
    if ($p) { Stop-SctPreferenceFixture $p }
    [IO.File]::WriteAllText($path,($log | ConvertTo-Json -Depth 4))
    Write-Host "Evidence: $path ($($log.outcome))"
}
if ($log.outcome -ne 'PASS') { throw "$($log.outcome) : $($log.error)" }
