
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Package,
    [switch]$PrepareOnly,
    [switch]$DedicatedSession
)
$ErrorActionPreference = 'Stop'
if ($PrepareOnly.IsPresent -eq $DedicatedSession.IsPresent) { throw 'Choose exactly one mode.' }
$zip = (Resolve-Path -LiteralPath $Package).ProviderPath
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$work = Join-Path (Join-Path $repo 'build') ('NOTIFY-001-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($work)
$evidence = Join-Path $work 'environment-result.json'
$settingsByCase = @(
    @{Name='renotify-off';Renotify=$false;ExpectedOnThird=0},
    @{Name='renotify-on';Renotify=$true;ExpectedOnThird=1}
)
$log = @{id='SCT-NOTIFY-001';outcome='PREPARED_ONLY';packageSha256=(Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
    windows=[Environment]::OSVersion.VersionString;atUtc=[DateTimeOffset]::UtcNow.ToString('o');observations=@();error=$null}
foreach ($mode in $settingsByCase) {
    $root = Join-Path $work $mode.Name
    [void][IO.Directory]::CreateDirectory($root)
    $mode.App = Join-Path $root 'product'
    $mode.Document = Join-Path $root ($mode.Name + '.txt')
    [IO.Compression.ZipFile]::ExtractToDirectory($zip,$mode.App)
    foreach ($n in 'settings.json','settings.json.bak','cache.json','cache.sqlite3','cache.sqlite3-wal','cache.sqlite3-shm') {
        if ([IO.File]::Exists((Join-Path $mode.App $n))) { throw "ZIP contains application state: $n." }
    }
    [IO.File]::WriteAllText($mode.Document,'starting contents')
    $config = @{Version=1;KeepInTray=$true;LowServerLoad=$false;PollSeconds=5;WaitForStable=$true;StableSeconds=20;RenotifyUnread=$mode.Renotify;
        Items=@(@{Id=[guid]::NewGuid().ToString();Path=$mode.Document;IsFolder=$false})}
    [IO.File]::WriteAllText((Join-Path $mode.App 'settings.json'),($config | ConvertTo-Json -Depth 4))
}
if ($PrepareOnly) {
    [IO.File]::WriteAllText($evidence,($log | ConvertTo-Json -Depth 4))
    Write-Host "Both notification variants prepared without running them: $work"
    return
}
if (![Environment]::UserInteractive -or ![Diagnostics.Process]::GetCurrentProcess().SessionId) { throw 'Use an isolated interactive Windows desktop.' }
$mutex = $null
if ([Threading.Mutex]::TryOpenExisting('Local\ShareFileWatch-v1',[ref]$mutex)) {
    $mutex.Dispose()
    throw 'Kawattayo is already running; the test will not close it.'
}
. (Join-Path $PSScriptRoot 'UnreadScenario.ps1')
. (Join-Path $PSScriptRoot 'MonitorLifecycle.ps1')
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

function Get-ObservedCount([string]$Question) {
    $answer = Read-Host $Question
    $count = 0
    if (![int]::TryParse($answer,[ref]$count) -or $count -lt 0) { throw 'Provide a nonnegative count copied from the dedicated recording.' }
    return $count
}
function Write-FixtureChange($Process,$View,[string]$Document,[string]$Contents,[int]$ExpectedUnread) {
    $now = [DateTimeOffset]::UtcNow
    [IO.File]::WriteAllText($Document,$Contents)
    Write-Host "Fixture update: $($now.ToString('o')), $([IO.Path]::GetFileName($Document)); expect $ExpectedUnread unread."
    Invoke-SctUiButton $View '↻ 更新'
    [void](Wait-SctUi "notification unread $ExpectedUnread" {
        $w = Get-SctOwnedWindow $Process.Id 'かわったよ v' -Prefix
        if ($w -and (Get-SctUiLabel $w "未読 $ExpectedUnread 件")) { $w }
    })
    return $now
}

$log.outcome = 'FAIL'
try {
    foreach ($variant in $settingsByCase) {
        $p = $null
        try {
            Write-Host "Record only the real notification banners for this file: $($variant.Document)"
            Read-Host 'Ensure recording and notification banners are enabled in the dedicated session, then press Enter'
            $p = Start-SctUnreadApp (Join-Path $variant.App 'かわったよ.exe')
            $view = Wait-SctUi 'notification fixture baseline' {
                $w = Get-SctOwnedWindow $p.Id 'かわったよ v' -Prefix
                if ($w -and (Get-SctUiLabel $w '最終巡回 ' -Prefix) -and (Get-SctUiLabel $w '未読 0 件')) { $w }
            }
            $first = Write-FixtureChange $p $view $variant.Document 'first fixture version' 1
            $second = Write-FixtureChange $p $view $variant.Document 'second version with a different length' 2
            if (($second - $first).TotalSeconds -ge 20) { throw 'The first two updates did not occur within the same stability window.' }
            $notBefore = $second.AddSeconds(20)
            Write-Host "Do not click or acknowledge. Before $($notBefore.ToString('o')), expect no banner for this file."
            Start-Sleep -Seconds 36
            $early = Get-ObservedCount "How many banners for this file were visible before $($notBefore.ToString('o'))?"
            $settled = Get-ObservedCount 'How many were visible after that time, before this prompt?'
            $third = Write-FixtureChange $p $view $variant.Document 'third version while previous change stays unread, with different size' 3
            Start-Sleep -Seconds 36
            $repeated = Get-ObservedCount "How many banners for this file appeared after $($third.ToString('o'))?"
            $log.observations += @{renotify=$variant.Renotify; firstUtc=$first.ToString('o');secondUtc=$second.ToString('o');
                thirdUtc=$third.ToString('o');beforeStable=$early;afterStable=$settled;whileUnread=$repeated}
            if ($early -ne 0 -or $settled -ne 1 -or $repeated -ne $variant.ExpectedOnThird) {
                throw "Observed banner counts do not match the timing and duplicate policy for $($variant.Name)."
            }
        } finally {
            if ($p) {
                # Only our disposable process; it intentionally stays in the notification area.
                if (!$p.HasExited) { $p.Kill(); [void]$p.WaitForExit(5000) }
                $p.Dispose()
            }
        }
    }
    $log.outcome = 'PASS'
} catch {
    $log.error = $_.Exception.Message
} finally {
    [IO.File]::WriteAllText($evidence,($log | ConvertTo-Json -Depth 5))
    Write-Host "Evidence: $evidence ($($log.outcome)); keep the recording separately."
}
if ($log.outcome -ne 'PASS') { throw $log.error }
