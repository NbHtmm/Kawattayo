
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
$work = Join-Path (Join-Path $repo 'build') ('NOTIFY-GUI-003-' + [guid]::NewGuid().ToString('N'))
$root = Join-Path $work 'NotifyFixture'
$folder = Join-Path $root 'nested'
[void][IO.Directory]::CreateDirectory($folder)
$install = Join-Path $work 'product'
[IO.Compression.ZipFile]::ExtractToDirectory($zip,$install)
foreach ($name in 'settings.json','settings.json.bak','cache.json','cache.sqlite3','cache.sqlite3-wal','cache.sqlite3-shm') {
    if ([IO.File]::Exists((Join-Path $install $name))) { throw "ZIP contains application state: $name." }
}
$file = Join-Path $folder 'notification-target.txt'
[IO.File]::WriteAllText($file,'initial content for notification')
$configuration = @{Version=1;KeepInTray=$true;LowServerLoad=$false;PollSeconds=5;WaitForStable=$false;
    Items=@(@{Id=[guid]::NewGuid().ToString();Path=$root;IsFolder=$true;Recursive=$true;RecursiveOverride=$true})}
[IO.File]::WriteAllText((Join-Path $install 'settings.json'),($configuration | ConvertTo-Json -Depth 4))
$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
if ($PrepareOnly) {
    [IO.File]::WriteAllText((Join-Path $work 'environment-result.json'),
        (@{id='SCT-GUI-003';outcome='PREPARED_ONLY';packageSha256=$hash} | ConvertTo-Json))
    Write-Host "Fixture prepared without starting the application or using Windows notifications: $work"
    return
}
if (![Environment]::UserInteractive -or ![Diagnostics.Process]::GetCurrentProcess().SessionId) {
    throw 'Only run the interactive notification case in a throwaway Windows session.'
}
$mutex = $null
if ([Threading.Mutex]::TryOpenExisting('Local\ShareFileWatch-v1',[ref]$mutex)) {
    $mutex.Dispose()
    throw 'Kawattayo is already running; the test will not close it.'
}
. (Join-Path $PSScriptRoot 'UnreadScenario.ps1')
. (Join-Path $PSScriptRoot 'MonitorLifecycle.ps1')
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$exe = Join-Path $install 'かわったよ.exe'
$p = $null
$outcome = 'FAIL'
$failure = $null
$triggerTime = $null
try {
    $p = Start-SctUnreadApp $exe
    $view = Wait-SctUi 'initial notification fixture scan' {
        $window = Get-SctOwnedWindow $p.Id 'かわったよ v' -Prefix
        if ($window -and (Get-SctUiLabel $window '最終巡回 ' -Prefix) -and (Get-SctUiLabel $window '未読 0 件')) { $window }
    }
    $request = Start-Process -FilePath $exe -WorkingDirectory $install -ArgumentList '--show-collector' -WindowStyle Hidden -PassThru
    try {
        if (!$request.WaitForExit(5000) -or $request.ExitCode -ne 0) { throw 'Unable to open collector in the existing test process.' }
    } finally {
        if (!$request.HasExited) { $request.Kill(); [void]$request.WaitForExit(5000) }
        $request.Dispose()
    }
    $collector = Wait-SctUi 'collector remains in same test process' { Get-SctOwnedWindow $p.Id 'かわったよ · 差分収集' }
    $handle = $collector.Current.NativeWindowHandle
    $element = [Windows.Automation.AutomationElement]
    $scope = [Windows.Automation.TreeScope]::Descendants
    $edit = $collector.FindFirst($scope,[Windows.Automation.PropertyCondition]::new($element::ControlTypeProperty,[Windows.Automation.ControlType]::Edit))
    $input = $edit.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern)
    $draft = 'draft must survive clicking the notification'
    $input.SetValue($draft)
    $view.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).Close()
    [void](Wait-SctUi 'monitor hidden but collector visible' {
        !(Get-SctOwnedWindow $p.Id 'かわったよ v' -Prefix) -and (Get-SctOwnedWindow $p.Id 'かわったよ · 差分収集')
    })

    Write-Host 'Click only the new real Windows notification for notification-target.txt. Do not open the monitor via the tray icon or launcher.'
    $triggerTime = [DateTimeOffset]::UtcNow.ToString('o')
    [IO.File]::WriteAllText($file,'new contents from native notification fixture')
    $timer = [Diagnostics.Stopwatch]::StartNew()
    do {
        $view = Get-SctOwnedWindow $p.Id 'かわったよ v' -Prefix
        if ($view) { break }
        Start-Sleep -Milliseconds 150
    } while ($timer.Elapsed.TotalSeconds -lt 55)
    if (!$view) {
        $outcome = 'BLOCKED'
        throw 'No notification activation was observed. Check OS notifications in the dedicated session.'
    }
    [void](Wait-SctUi 'exact nested file selected in activated monitor' {
        $type = [Windows.Automation.PropertyCondition]::new($element::ControlTypeProperty,[Windows.Automation.ControlType]::ListItem)
        foreach ($row in $view.FindAll($scope,$type)) {
            if (!(Get-SctUiLabel $row 'notification-target.txt を既読にする')) { continue }
            $selection = $null
            if ($row.TryGetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern,[ref]$selection) -and
                $selection.Current.IsSelected) { return $true }
        }
        return $false
    })
    $collectorNow = Get-SctOwnedWindow $p.Id 'かわったよ · 差分収集'
    if (!$collectorNow -or $collectorNow.Current.NativeWindowHandle -ne $handle -or $input.Current.Value -cne $draft) {
        throw 'The collector or its unsaved input was not preserved.'
    }
    $outcome = 'PASS'
} catch {
    $failure = $_.Exception.Message
} finally {
    if ($p) {
        # Only this package's disposable process may still own a tray icon. Never use global input.
        if (!$p.HasExited) { $p.Kill(); [void]$p.WaitForExit(5000) }
        $p.Dispose()
    }
    $log = @{id='SCT-GUI-003';outcome=$outcome;packageSha256=$hash;changedAtUtc=$triggerTime;
        windows=[Environment]::OSVersion.VersionString;requiresOperatorClick=$true; exactSelectionObserved=($outcome -eq 'PASS');error=$failure}
    [IO.File]::WriteAllText((Join-Path $work 'environment-result.json'),($log | ConvertTo-Json))
    Write-Host "Evidence: $work ($outcome)"
}
if ($outcome -ne 'PASS') { throw "$outcome : $failure" }
