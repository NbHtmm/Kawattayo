
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Package,
    [switch]$DedicatedSession,
    [switch]$PrepareOnly
)
$ErrorActionPreference = 'Stop'
if ($DedicatedSession.IsPresent -eq $PrepareOnly.IsPresent) { throw 'Choose exactly one run mode.' }
$zip = (Resolve-Path -LiteralPath $Package).ProviderPath
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$work = Join-Path (Join-Path $repo 'build') ('EXPLORER-GUI-015-' + [guid]::NewGuid().ToString('N'))
$fixture = Join-Path $work 'DropFromHere'
$folder = Join-Path $fixture 'drop-folder'
$file = Join-Path $fixture 'drop-file.txt'
[void][IO.Directory]::CreateDirectory($folder)
[IO.File]::WriteAllText($file,'ordinary document for drag and drop')
[IO.File]::WriteAllText((Join-Path $folder 'inside.txt'),'folder child')
$install = Join-Path $work 'product'
[IO.Compression.ZipFile]::ExtractToDirectory($zip,$install)
$settingsFile = Join-Path $install 'settings.json'
foreach ($name in 'settings.json','settings.json.bak','cache.json','cache.sqlite3','cache.sqlite3-wal','cache.sqlite3-shm') {
    if ([IO.File]::Exists((Join-Path $install $name))) { throw "ZIP contains application state: $name." }
}
[IO.File]::WriteAllText($settingsFile,'{"Version":1,"KeepInTray":false,"PollSeconds":5,"Items":[]}')
$log = @{id='SCT-GUI-015';outcome='PREPARED_ONLY';packageSha256=(Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
    windows=[Environment]::OSVersion.VersionString;atUtc=[DateTimeOffset]::UtcNow.ToString('o'); error=$null;observations=@() }
$resultPath = Join-Path $work 'environment-result.json'
if ($PrepareOnly) {
    [IO.File]::WriteAllText($resultPath,($log | ConvertTo-Json -Depth 5))
    Write-Host "Fixture prepared without starting an application: $fixture"
    return
}
if (![Environment]::UserInteractive -or ![Diagnostics.Process]::GetCurrentProcess().SessionId) {
    throw 'An isolated interactive Windows session is required.'
}
$mutex = $null
if ([Threading.Mutex]::TryOpenExisting('Local\ShareFileWatch-v1',[ref]$mutex)) {
    $mutex.Dispose()
    throw 'Kawattayo is already running; the test will not close it.'
}
. (Join-Path $PSScriptRoot 'UnreadScenario.ps1')
. (Join-Path $PSScriptRoot 'MonitorLifecycle.ps1')
. (Join-Path $PSScriptRoot 'PreferencePersistence.ps1')
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$p = $null
$log.outcome = 'FAIL'
try {
    $p = Start-SctUnreadApp (Join-Path $install 'かわったよ.exe')
    $main = Wait-SctUi 'empty monitor' {
        $window = Get-SctOwnedWindow $p.Id 'かわったよ v' -Prefix
        if ($window -and (Get-SctUiLabel $window '未読 0 件')) { $window }
    }
    $settings = Open-SctPreferenceWindow $main $p
    $before = Get-Content -Raw -LiteralPath $settingsFile | ConvertFrom-Json
    if ($before.Items.Count -ne 0) { throw 'The initial fixture was not empty.' }
    $ids = @{}
    foreach ($phase in 'first drop','repeat drop') {
        $beforePoll = (Get-SctUiLabel $main '最終巡回 ' -Prefix).Current.Name
        Read-Host "Open $fixture in Explorer, select drop-file.txt and drop-folder, perform the $phase onto the test app settings window, then press Enter"
        [void](Wait-SctUi 'both dropped entries shown' {
            (Get-SctUiLabel $settings 'drop-file.txt') -and (Get-SctUiLabel $settings 'drop-folder') -and
                (Get-SctUiLabel $settings '登録 2 件')
        })

        Invoke-SctUiButton $main '↻ 更新'
        [void](Wait-SctUi 'force a new completed scan after the drop' {
            $poll = Get-SctUiLabel $main '最終巡回 ' -Prefix
            $poll -and $poll.Current.Name -ne $beforePoll
        })
        $saved = Wait-SctUi 'both dropped entries persisted' {
            try { $state = Get-Content -Raw -LiteralPath $settingsFile | ConvertFrom-Json }
            catch { return $null }
            if ($state.Items.Count -eq 2) { $state }
        }
        foreach ($expected in @(@{Path=$file;IsFolder=$false},@{Path=$folder;IsFolder=$true})) {
            $matches = @($saved.Items | Where-Object { $_.Path -ieq $expected.Path -and $_.IsFolder -eq $expected.IsFolder })
            $parsed = [guid]::Empty
            if ($matches.Count -ne 1 -or ![guid]::TryParse($matches[0].Id,[ref]$parsed) -or $parsed -eq [guid]::Empty) {
                throw 'The expected target was not registered exactly once with a valid ID and kind.'
            }
            if ($phase -eq 'first drop') { $ids[$expected.Path] = $matches[0].Id }
            elseif ($ids[$expected.Path] -ne $matches[0].Id) { throw 'Repeated drop changed an existing ID.' }
        }
        $log.observations += @{ phase=$phase; registryEntries=2; sameIds=$true }
    }
    if ([IO.File]::ReadAllText($file) -cne 'ordinary document for drag and drop' -or
        [IO.File]::ReadAllText((Join-Path $folder 'inside.txt')) -cne 'folder child') { throw 'Drag and drop altered a source file.' }
    $log.outcome = 'PASS'
} catch {
    $log.error = $_.Exception.Message
} finally {
    if ($p) { Stop-SctPreferenceFixture $p }
    [IO.File]::WriteAllText($resultPath,($log | ConvertTo-Json -Depth 5))
    Write-Host "Evidence: $resultPath ($($log.outcome))"
}
if ($log.outcome -ne 'PASS') { throw $log.error }
