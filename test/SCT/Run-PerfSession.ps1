
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Package,
    [ValidateRange(128,45000)][int]$Files = 16000,
    [ValidateRange(250,60000)][int]$MaxUiMilliseconds = 2000,
    [switch]$DedicatedSession,
    [switch]$LocalRehearsal
)
$ErrorActionPreference = 'Stop'
if ($DedicatedSession.IsPresent -eq $LocalRehearsal.IsPresent) { throw 'Choose exactly one run mode.' }
if ($DedicatedSession -and $Files -lt 8000) { throw 'Use at least 8,000 test files for SCT-PERF-001.' }
$session = [Diagnostics.Process]::GetCurrentProcess().SessionId
if (!$session -or ![Environment]::UserInteractive) { throw 'An isolated interactive Windows session is required.' }
$mutex = $null
if ([Threading.Mutex]::TryOpenExisting('Local\ShareFileWatch-v1',[ref]$mutex)) {
    $mutex.Dispose()
    throw 'Kawattayo is already running; the benchmark will not close it.'
}

. (Join-Path $PSScriptRoot 'UnreadScenario.ps1')
. (Join-Path $PSScriptRoot 'MonitorLifecycle.ps1')
. (Join-Path $PSScriptRoot 'PreferencePersistence.ps1')
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$work = Join-Path $repo ('build/PERF-001-' + [guid]::NewGuid().ToString('N'))
$root = Join-Path $work 'PerfFixture'
[void][IO.Directory]::CreateDirectory($root)
$install = Join-Path $work 'product'
$zip = (Resolve-Path -LiteralPath $Package).ProviderPath
[IO.Compression.ZipFile]::ExtractToDirectory($zip,$install)
foreach ($name in 'settings.json','settings.json.bak','cache.json','cache.sqlite3','cache.sqlite3-wal','cache.sqlite3-shm') {
    if ([IO.File]::Exists((Join-Path $install $name))) { throw "ZIP contains application state: $name." }
}
for ($i = 0; $i -lt $Files; $i++) {
    $group = 'group-{0:D3}' -f [int][Math]::Floor($i / 100)
    $folder = Join-Path $root $group
    if ($i % 100 -eq 0) { [void][IO.Directory]::CreateDirectory($folder) }
    [IO.File]::WriteAllText((Join-Path $folder ('{0:D5}.txt' -f $i)),'fixture')
}
$config = @{ Version=1;KeepInTray=$false;LowServerLoad=$false;PollSeconds=3600;WaitForStable=$false;
    Items=@(@{Id=[guid]::NewGuid().ToString();Path=$root;IsFolder=$true;Recursive=$true}) }
[IO.File]::WriteAllText((Join-Path $install 'settings.json'),($config | ConvertTo-Json -Depth 4))

$p = $null
$outcome = 'FAIL'
$errorText = $null
$settingsMs = $null
$pauseMs = $null
$countAfter = $null
function Request-ObservedScan($Process,$View) {
    for ($attempt = 0; $attempt -lt 3; $attempt++) {
        $prior = (Get-SctUiLabel $View '最終巡回 ' -Prefix).Current.Name
        Invoke-SctUiButton $View '↻ 更新'
        $clock = [Diagnostics.Stopwatch]::StartNew()
        do {
            $current = Get-SctOwnedWindow $Process.Id 'かわったよ v' -Prefix
            if ($current -and (Get-SctUiLabel $current '照会中 ' -Prefix)) { return $current }
            $last = if ($current) { Get-SctUiLabel $current '最終巡回 ' -Prefix }
            if ($last -and $last.Current.Name -ne $prior) { break }
            Start-Sleep -Milliseconds 30
        } while ($clock.ElapsedMilliseconds -lt 2400)
    }
    return $null
}

try {
    $p = Start-SctUnreadApp (Join-Path $install 'かわったよ.exe')
    $view = Wait-SctUi 'benchmark baseline' {
        $window = Get-SctOwnedWindow $p.Id 'かわったよ v' -Prefix
        if ($window -and (Get-SctUiLabel $window '最終巡回 ' -Prefix) -and (Get-SctUiLabel $window '未読 0 件')) { $window }
    }
    $scanning = Request-ObservedScan $p $view
    if (!$scanning) {
        $outcome = 'BLOCKED'
        throw 'A scan could not be observed in progress; timing a command after a scan would not test responsiveness.'
    }
    $measure = [Diagnostics.Stopwatch]::StartNew()
    $settings = Open-SctPreferenceWindow $scanning $p
    $settingsMs = [int]$measure.ElapsedMilliseconds
    if ($settingsMs -gt $MaxUiMilliseconds) { throw "Settings took ${settingsMs}ms; limit is ${MaxUiMilliseconds}ms." }

    $tab = Get-SctUiLabel $settings '動作と表示'
    $tab.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
    [void](Wait-SctUi 'pause control ready' { Get-SctUiLabel $settings '一時停止' })
    [void](Wait-SctUi 'ready to issue another scan' { Get-SctUiLabel $view '最終巡回 ' -Prefix })
    if (!(Request-ObservedScan $p $view)) {
        $outcome = 'BLOCKED'
        throw 'Could not observe the second scan, so cancellation responsiveness was not measured.'
    }
    $measure.Restart()
    Invoke-SctUiButton $settings '一時停止'
    [void](Wait-SctUi 'monitor confirms cancellation' { Get-SctUiLabel $view '一時停止中' })
    $pauseMs = [int]$measure.ElapsedMilliseconds
    if ($pauseMs -gt $MaxUiMilliseconds) { throw "Cancellation took ${pauseMs}ms; limit is ${MaxUiMilliseconds}ms." }

    $added = Join-Path $root 'added-while-paused.txt'
    [IO.File]::WriteAllText($added,'created once during pause')
    Invoke-SctUiButton $settings '監視を再開'
    [void](Wait-SctUi 'resume reports one updated target' {
        (Get-SctUiLabel $view '未読 1 件') -and (Get-SctUiLabel $view '最終巡回 ' -Prefix)
    })
    Invoke-SctUiButton $view 'すべて展開'
    [void](Wait-SctUi 'the correct unread file after resume' { Get-SctUiLabel $view 'added-while-paused.txt を既読にする' })
    $settings = Open-SctPreferenceWindow $view $p
    # Switch from Preferences back to the table, then confirm the scanner's displayed count.
    $monitorTab = Get-SctUiLabel $settings '監視対象'
    $monitorTab.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
    $countAfter = $Files + 1
    [void](Wait-SctUi 'scanner reports complete target inventory' { Get-SctUiLabel $settings ('{0:N0}' -f $countAfter) })
    $outcome = 'PASS'
} catch {
    $errorText = $_.Exception.Message
} finally {
    if ($p) { Stop-SctPreferenceFixture $p }
    if ($LocalRehearsal) { $outcome = 'REHEARSAL_' + $outcome }
    $log = @{
        id='SCT-PERF-001'; outcome=$outcome; files=$Files; grouping=100; countAfter=$countAfter
        maxUiMs=$MaxUiMilliseconds; settingsUiMs=$settingsMs; pauseUiMs=$pauseMs
        windows=[Environment]::OSVersion.VersionString; logicalProcessors=[Environment]::ProcessorCount
        fixtureVolume=([IO.DriveInfo]::new([IO.Path]::GetPathRoot($root))).DriveType.ToString()
        atUtc=[DateTimeOffset]::UtcNow.ToString('o'); packageSha256=(Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
        error=$errorText
    }
    [IO.File]::WriteAllText((Join-Path $work 'environment-result.json'),($log | ConvertTo-Json -Depth 3))
    Write-Host "Evidence: $work ($outcome)"
}
if ($outcome -notin 'PASS','REHEARSAL_PASS') { throw "$outcome : $errorText" }
