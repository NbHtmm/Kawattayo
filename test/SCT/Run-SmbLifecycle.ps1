[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Monitor','Writer')][string]$Role,
    [Parameter(Mandatory)][string]$FixtureRoot,
    [Parameter(Mandatory)][guid]$RunId,
    [string]$Package,
    [switch]$DedicatedShare,
    [switch]$LocalRehearsal
)
$ErrorActionPreference = 'Stop'
if ($RunId -eq [guid]::Empty) { throw 'Supply a new run ID.' }
if ($DedicatedShare.IsPresent -eq $LocalRehearsal.IsPresent) {
    throw 'Choose either -DedicatedShare for an isolated UNC share or -LocalRehearsal for this checkout.'
}
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$build = [IO.Path]::GetFullPath((Join-Path $repo 'build'))
$root = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $FixtureRoot).ProviderPath)
if ($LocalRehearsal) {
    if (!$root.StartsWith($build + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Local rehearsal requires a fresh folder underneath the repository build folder.'
    }
} elseif ($root -notmatch '^\\\\[^\\]+\\[^\\]+\\Kawattayo-Sct\\?$') {
    throw 'Use a dedicated UNC folder named Kawattayo-Sct; this script does not create or change shares.'
}

$fixture = Join-Path $root ('run-' + $RunId.ToString('N'))
$control = Join-Path $fixture 'control'
$data = Join-Path $fixture 'data'
$file = Join-Path $data 'lifecycle.txt'
$renamed = Join-Path $data 'renamed.txt'
$mode = if ($LocalRehearsal) { 'local-rehearsal' } else { 'dedicated-smb' }

function Assert-TestDirectory([string]$Path) {
    $item = Get-Item -LiteralPath $Path
    if (!$item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Expected an ordinary test directory: $Path."
    }
}

function Write-Stage([string]$Stage,[string]$Value) {
    $temp = Join-Path $control ($Stage + '.' + [guid]::NewGuid().ToString('N') + '.tmp')
    # The rename only publishes a new marker; it cannot replace an earlier phase.
    $target = Join-Path $control ($Stage + '.txt')
    $stream = [IO.File]::Open($temp,[IO.FileMode]::CreateNew)
    try {
        $bytes = [Text.Encoding]::UTF8.GetBytes($Value)
        $stream.Write($bytes,0,$bytes.Length)
    } finally { $stream.Dispose() }
    [IO.File]::Move($temp,$target)
}

function Read-Stage([string]$Stage) {
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $target = Join-Path $control ($Stage + '.txt')
    while ($clock.Elapsed.TotalSeconds -lt 45) {
        if ([IO.File]::Exists($target)) { return [IO.File]::ReadAllText($target) }
        Start-Sleep -Milliseconds 150
    }
    throw "Timed out waiting for stage: $Stage."
}

function Assert-Stage([string]$Stage,[string]$Expected) {
    if ((Read-Stage $Stage) -cne $Expected) { throw "Unexpected stage value: $Stage." }
}

Assert-TestDirectory $root
if ($Role -eq 'Writer') {
    foreach ($path in $fixture,$control,$data) { Assert-TestDirectory $path }
    $owner = Read-Stage 'monitor'
    if (!$LocalRehearsal -and $owner -ieq $env:COMPUTERNAME) {
        throw 'Run Writer on the second Windows client, not on the monitoring client.'
    }
    Assert-Stage 'mode' $mode
    Write-Stage 'writer' $env:COMPUTERNAME
    Assert-Stage 'go-update' 'ready'
    if (([IO.File]::GetAttributes($file) -band [IO.FileAttributes]::ReparsePoint) -or
        [IO.File]::ReadAllText($file) -cne 'baseline') {
        throw 'The fixture file was replaced or does not have the expected contents.'
    }
    $stream = [IO.File]::Open($file,[IO.FileMode]::Open,[IO.FileAccess]::Write,[IO.FileShare]::None)
    try {
        Write-Stage 'writer-holds-lock' 'ready'
        Assert-Stage 'monitored-while-open' 'ready'
        $bytes = [Text.Encoding]::UTF8.GetBytes('writer changed the fixture contents')
        # The scanner must finish while the writer holds an exclusive handle. Only then write
        # once so a single logical change cannot be split into truncated and complete versions.
        $stream.Write($bytes,0,$bytes.Length)
        $stream.Flush()
    } finally { $stream.Dispose() }
    Write-Stage 'did-update' 'ready'
    Assert-Stage 'go-rename' 'ready'
    [IO.File]::Move($file,$renamed)
    Write-Stage 'did-rename' 'ready'
    Assert-Stage 'go-restore' 'ready'
    [IO.File]::Move($renamed,$file)
    Write-Stage 'did-restore' 'ready'
    Assert-Stage 'go-delete' 'ready'
    [IO.File]::Delete($file) # One exact fixture file; never removes a folder or tests arbitrary targets.
    if ([IO.File]::Exists($file) -or [IO.File]::Exists($renamed)) { throw 'Fixture file remains after deletion.' }
    Write-Stage 'did-delete' 'ready'
    return
}

if (!$Package) { throw 'Monitor requires the distribution ZIP.' }
$zip = (Resolve-Path -LiteralPath $Package).ProviderPath
if ([IO.Directory]::Exists($fixture) -or [IO.File]::Exists($fixture)) { throw 'Run ID already exists; use a fresh ID.' }
$session = [Diagnostics.Process]::GetCurrentProcess().SessionId
if (!$session -or ![Environment]::UserInteractive) { throw 'Use a dedicated interactive Windows session for Monitor.' }
$mutex = $null
if ([Threading.Mutex]::TryOpenExisting('Local\ShareFileWatch-v1',[ref]$mutex)) {
    $mutex.Dispose()
    throw 'Kawattayo is already running. Stop the existing instance yourself in the dedicated session.'
}

. (Join-Path $PSScriptRoot 'UnreadScenario.ps1')
. (Join-Path $PSScriptRoot 'MonitorLifecycle.ps1')
. (Join-Path $PSScriptRoot 'PreferencePersistence.ps1')
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$work = Join-Path $build ('SMB-002-' + $RunId.ToString('N'))
if ([IO.Directory]::Exists($work)) { throw 'Local result directory already exists.' }
[void][IO.Directory]::CreateDirectory($work)
$install = Join-Path $work 'product'
[IO.Compression.ZipFile]::ExtractToDirectory($zip,$install)
$settings = Join-Path $install 'settings.json'
foreach ($name in 'settings.json','settings.json.bak','cache.json','cache.sqlite3','cache.sqlite3-wal','cache.sqlite3-shm') {
    if (Test-Path -LiteralPath (Join-Path $install $name)) { throw "ZIP contains pre-existing application state: $name." }
}
[void][IO.Directory]::CreateDirectory($control)
[void][IO.Directory]::CreateDirectory($data)
[IO.File]::WriteAllText($file,'baseline')
$configuration = @{ Version=1;KeepInTray=$false;LowServerLoad=$false;PollSeconds=5;NotifyDeleted=$true;WaitForStable=$false;
    Items=@(@{Id=[guid]::NewGuid().ToString();Path=$file;IsFolder=$false}) }
[IO.File]::WriteAllText($settings,($configuration | ConvertTo-Json -Depth 4))
Write-Stage 'monitor' $env:COMPUTERNAME
Write-Stage 'mode' $mode
$started = [DateTimeOffset]::UtcNow
$otherClient = $null
$outcome = 'FAIL'
$problem = $null
$events = [Collections.Generic.List[string]]::new()
$p = $null
try {
    Write-Host "Fixture is ready for Writer. RunId: $RunId"
    $p = Start-SctUnreadApp (Join-Path $install 'かわったよ.exe')
    $view = Wait-SctUi 'network fixture baseline' {
        $w = Get-SctMonitorWindow $p
        if ($w -and (Get-SctUiLabel $w '最終巡回 ' -Prefix) -and (Get-SctUiLabel $w '未読 0 件')) { $w }
    }
    $otherClient = Read-Stage 'writer'
    if (!$LocalRehearsal -and $otherClient -ieq $env:COMPUTERNAME) { throw 'Writer must use a separate client.' }
    Write-Stage 'go-update' 'ready'
    Assert-Stage 'writer-holds-lock' 'ready'
    # Use a completed scan's clock indication while the other client still owns the exclusive handle.
    $prior = (Get-SctUiLabel $view '最終巡回 ' -Prefix).Current.Name
    Invoke-SctUiButton $view '↻ 更新'
    $view = Wait-SctUi 'completed scan during exclusive write' {
        $w = Get-SctMonitorWindow $p
        $stamp = if ($w) { Get-SctUiLabel $w '最終巡回 ' -Prefix }
        if ($stamp -and $stamp.Current.Name -cne $prior) { $w }
    }
    Write-Stage 'monitored-while-open' 'ready'
    Assert-Stage 'did-update' 'ready'
    $view = Wait-SctFileEvent $p $view '更新'
    $events.Add('exclusive writer completed with monitored update')
    foreach ($phase in @(@{Step='rename'; Kind='消失'},@{Step='restore'; Kind='出現'},@{Step='delete'; Kind='消失'})) {
        Write-Stage ('go-' + $phase.Step) 'ready'
        Assert-Stage ('did-' + $phase.Step) 'ready'
        $view = Wait-SctFileEvent $p $view $phase.Kind
        $events.Add($phase.Step + ': ' + $phase.Kind)
    }
    if ([IO.File]::Exists($file) -or [IO.File]::Exists($renamed)) { throw 'Monitor and Writer disagree on final file state.' }

    Invoke-SctUiButton $view '履歴'
    [void](Wait-SctUi 'all four lifecycle events recorded exactly once per observed operation' {
        $history = Get-SctOwnedWindow $p.Id 'かわったよ | 変更履歴'
        if (!$history) { return $false }
        $property = [Windows.Automation.AutomationElement]::NameProperty
        $scope = [Windows.Automation.TreeScope]::Descendants
        foreach ($expected in @(@{Name='更新';Count=1},@{Name='出現';Count=1},@{Name='消失';Count=2},@{Name=$file;Count=4})) {
            $hits = @($history.FindAll($scope,[Windows.Automation.PropertyCondition]::new($property,$expected.Name)))
            if ($hits.Count -ne $expected.Count) { return $false }
        }
        return $true
    })
    $outcome = if ($LocalRehearsal) { 'REHEARSAL_PASS' } else { 'PASS' }
} catch {
    $problem = $_.Exception.Message
    throw
} finally {
    if ($p) { Stop-SctPreferenceFixture $p }
    $record = @{
        id='SCT-SMB-002'; outcome=$outcome; mode=$mode; startedUtc=$started.ToString('o')
        finishedUtc=[DateTimeOffset]::UtcNow.ToString('o'); windows=[Environment]::OSVersion.VersionString
        packageSha256=(Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
        separateClients=($null -ne $otherClient -and $otherClient -ine $env:COMPUTERNAME)
        observations=@($events.ToArray()); error=$problem
    }
    [IO.File]::WriteAllText((Join-Path $work 'environment-result.json'),($record | ConvertTo-Json -Depth 4))
    Write-Host "Evidence: $work"
}
