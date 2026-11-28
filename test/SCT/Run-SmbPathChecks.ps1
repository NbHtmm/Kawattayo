[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Package,
    [Parameter(Mandatory)][string]$FixtureRoot,
    [Parameter(Mandatory)][string]$LongRelativePath,
    [ValidateRange(120,1000)][int]$MinLongPath = 260,
    [switch]$DedicatedShare,
    [switch]$PrepareOnly
)
$ErrorActionPreference = 'Stop'
if ($DedicatedShare.IsPresent -eq $PrepareOnly.IsPresent) { throw 'Choose exactly one run mode.' }
if ($FixtureRoot -notmatch '^\\\\[^\\]+\\[^\\]+\\Kawattayo-Sct\\Paths\\?$') {
    throw 'Specify a dedicated UNC fixture named Kawattayo-Sct\Paths.'
}
$child = $LongRelativePath.Replace('/','\')
if ($child -notmatch '^readable\\.+' -or [IO.Path]::IsPathRooted($child) -or
    $child -match '(^|\\)\.{1,2}(\\|$)|[:*?|"<>]' -or $child.EndsWith('\')) {
    throw 'Choose a plain relative filename under readable, not an escape or alternate stream.'
}
$root = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($FixtureRoot))
$long = [IO.Path]::GetFullPath([IO.Path]::Combine($root,$child))
$normal = [IO.Path]::Combine($root,'readable\測定.txt')
$absent = [IO.Path]::Combine($root,'readable\absent-file.txt')
$denied = [IO.Path]::Combine($root,'denied')
if ($long.Length -lt $MinLongPath -or $long -ieq $normal -or $long -ieq $absent -or
    !$long.StartsWith($root + '\readable\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid dedicated long-path fixture.' }
$zip = (Resolve-Path -LiteralPath $Package).ProviderPath
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$work = Join-Path (Join-Path $repo 'build') ('SMB-003-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($work)
$log = @{id='SCT-SMB-003';outcome='PREPARED_ONLY';packageSha256=(Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash;
    longPathLength=$long.Length; expected=@{Normal='正常';Long='正常';Absent='対象なし';Denied='監視失敗'}
    windows=[Environment]::OSVersion.VersionString;atUtc=[DateTimeOffset]::UtcNow.ToString('o');error=$null}
$logPath = Join-Path $work 'environment-result.json'

if ($PrepareOnly) {
    [IO.File]::WriteAllText($logPath,($log | ConvertTo-Json -Depth 4))
    Write-Host "Fixture contract recorded without accessing the UNC share: $logPath"
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
    foreach ($entry in $normal,$long) {
        if (![IO.File]::Exists($entry)) { throw 'Expected dedicated normal and long-path files to exist.' }
    }
    if ([IO.File]::Exists($absent) -or [IO.Directory]::Exists($absent)) { throw 'The absent filename must not exist.' }
    try {
        $iterator = [IO.Directory]::EnumerateFileSystemEntries($denied).GetEnumerator()
        try { [void]$iterator.MoveNext() } finally { $iterator.Dispose() }
        throw 'Denied folder was readable. An administrator must prepare the dedicated ACL beforehand.'
    } catch [UnauthorizedAccessException] { }
    $log.outcome = 'FAIL'

    . (Join-Path $PSScriptRoot 'UnreadScenario.ps1')
    . (Join-Path $PSScriptRoot 'MonitorLifecycle.ps1')
    . (Join-Path $PSScriptRoot 'PreferencePersistence.ps1')
    Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
    $install = Join-Path $work 'product'
    [IO.Compression.ZipFile]::ExtractToDirectory($zip,$install)
    foreach ($name in 'settings.json','settings.json.bak','cache.json','cache.sqlite3','cache.sqlite3-wal','cache.sqlite3-shm') {
        if ([IO.File]::Exists((Join-Path $install $name))) { throw "ZIP contains application state: $name." }
    }
    $configured = @{Version=1;KeepInTray=$false;LowServerLoad=$false;PollSeconds=5;WaitForStable=$false;NotifyDeleted=$true;
        Items=@(
            @{Id=[guid]::NewGuid().ToString();DisplayName='SCT normal';Path=$normal;IsFolder=$false},
            @{Id=[guid]::NewGuid().ToString();DisplayName='SCT long';Path=$long;IsFolder=$false},
            @{Id=[guid]::NewGuid().ToString();DisplayName='SCT absent';Path=$absent;IsFolder=$false},
            @{Id=[guid]::NewGuid().ToString();DisplayName='SCT denied';Path=$denied;IsFolder=$true;Recursive=$true}
        )}
    [IO.File]::WriteAllText((Join-Path $install 'settings.json'),($configured | ConvertTo-Json -Depth 5))
    $p = Start-SctUnreadApp (Join-Path $install 'かわったよ.exe')
    $main = Wait-SctUi 'one unavailable folder without invented disappearance' {
        $w = Get-SctOwnedWindow $p.Id 'かわったよ v' -Prefix
        if ($w -and (Get-SctUiLabel $w '最終巡回 ' -Prefix) -and (Get-SctUiLabel $w '未読 0 件') -and
            (Get-SctUiLabel $w '監視失敗 1件' -Prefix)) { $w }
    }
    $settings = Open-SctPreferenceWindow $main $p
    $labels = @{ 'SCT normal'='正常';'SCT long'='正常';'SCT absent'='対象なし';'SCT denied'='監視失敗' }
    [void](Wait-SctUi 'distinct status for all four dedicated UNC targets' {
        $scope = [Windows.Automation.TreeScope]::Descendants
        $items = [Windows.Automation.PropertyCondition]::new(
            [Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::ListItem)
        $rows = @($settings.FindAll($scope,$items))
        foreach ($name in $labels.Keys) {
            $found = @($rows | Where-Object { (Get-SctUiLabel $_ $name) -and (Get-SctUiLabel $_ $labels[$name]) })
            if ($found.Count -ne 1) { return $false }
        }
        return $true
    })
    $log.outcome = 'PASS'
} catch {
    $log.error = $_.Exception.Message
} finally {
    if ($p) { Stop-SctPreferenceFixture $p }
    [IO.File]::WriteAllText($logPath,($log | ConvertTo-Json -Depth 4))
    Write-Host "Evidence: $logPath ($($log.outcome))"
}
if ($log.outcome -ne 'PASS') { throw "$($log.outcome) : $($log.error)" }
