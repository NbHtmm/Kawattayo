# Test the packaged GUI through Windows UI Automation. Only the fixture's settings are seeded.
function Invoke-SctUnreadScenario([string]$Package, [string]$Work) {
    Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
    [void][IO.Directory]::CreateDirectory($Work)
    $app = Join-Path $Work 'app'
    [IO.Compression.ZipFile]::ExtractToDirectory($Package, $app)
    $exe = Join-Path $app 'かわったよ.exe'
    $targets = @()
    $paths = @()
    foreach ($name in 'SCT-Alpha','SCT-Beta') {
        $root = Join-Path $Work $name
        $subfolder = Join-Path $root 'nested'
        [void][IO.Directory]::CreateDirectory($subfolder)
        $targets += @{ Id = [guid]::NewGuid().ToString(); Path = $root; IsFolder = $true; Recursive = $true; RecursiveOverride = $true }
        $paths += Join-Path $subfolder ($name + '.txt')
    }
    $evidence = Join-Path $Work 'observations.txt'
    $settings = @{ Version = 1; Items = $targets; KeepInTray = $false; PollSeconds = 5; LowServerLoad = $false }
    $settingsFile = Join-Path $app 'settings.json'
    $stream = [IO.File]::Open($settingsFile, [IO.FileMode]::CreateNew)
    try {
        $bytes = [Text.Encoding]::UTF8.GetBytes(($settings | ConvertTo-Json -Depth 5))
        $stream.Write($bytes, 0, $bytes.Length)
    } finally { $stream.Dispose() }

    $p = $null
    try {
        $p = Start-SctUnreadApp $exe
        $view = Wait-SctUi 'initial scan completed with no unread items' {
            $main = Get-SctMonitorWindow $p
            if ($main -and (Get-SctUiLabel $main '未読 0 件') -and (Get-SctUiLabel $main '最終巡回 ' -Prefix)) { $main }
        }
        [IO.File]::AppendAllText($evidence, "PASS baseline: unread=0
")
        foreach ($path in $paths) { [IO.File]::WriteAllText($path, 'SCT fixture') }
        Invoke-SctUiButton $view '↻ 更新'
        $view = Wait-SctUi 'two distinct unread updates' {
            $main = Get-SctMonitorWindow $p
            if ($main -and (Get-SctUiLabel $main '未読 2 件') -and
                (Get-SctUiLabel $main 'SCT-Alpha を既読にする') -and (Get-SctUiLabel $main 'SCT-Beta を既読にする')) { $main }
        }
        if ((Get-SctUiLabel $view 'SCT-Alpha.txt') -or (Get-SctUiLabel $view 'SCT-Beta.txt')) {
            throw 'Unread folder roots must be collapsed initially.'
        }
        Invoke-SctUiButton $view 'すべて展開'
        $view = Wait-SctUi 'expanded tree shows both changed files' {
            $main = Get-SctMonitorWindow $p
            if ($main -and (Get-SctUiLabel $main 'SCT-Alpha.txt') -and (Get-SctUiLabel $main 'SCT-Beta.txt')) { $main }
        }
        [IO.File]::AppendAllText($evidence, "PASS unread=2; initially collapsed; both files visible after expand
")
        Invoke-SctUiButton $view 'SCT-Alpha を既読にする'
        Wait-SctRemainingUnread $p
        foreach ($path in $paths) {
            if (![IO.File]::Exists($path) -or [IO.File]::ReadAllText($path) -cne 'SCT fixture') {
                throw "Acknowledging an update modified a monitored file: $path."
            }
        }
        [IO.File]::AppendAllText($evidence, "PASS after acknowledgment: only SCT-Beta remains; both source files unchanged
")
        if (!$p.CloseMainWindow() -or !$p.WaitForExit(10000) -or $p.ExitCode -ne 0) {
            throw 'The fixture did not shut down normally before restart.'
        }
        $p.Dispose()
        $p = $null
        $p = Start-SctUnreadApp $exe
        Wait-SctRemainingUnread $p -AfterScan
        [IO.File]::AppendAllText($evidence, "PASS after restart and scan: only SCT-Beta remains
")
    } catch {
        try {
            $window = if ($p) { Get-SctMonitorWindow $p }
            if ($window) {
                $scope = [Windows.Automation.TreeScope]::Descendants
                $rows = @($window.FindAll($scope, [Windows.Automation.Condition]::TrueCondition) |
                    ForEach-Object { "$($_.Current.ControlType.ProgrammaticName): $($_.Current.Name)" })
                [IO.File]::WriteAllLines((Join-Path $Work 'ui-on-failure.txt'), [string[]]$rows)
            }
        } catch { Write-Warning "Could not capture GUI failure evidence: $($_.Exception.Message)" }
        throw
    } finally {
        if ($p) {
            try {
                if (!$p.HasExited -and !($p.CloseMainWindow() -and $p.WaitForExit(5000))) {
                    # Only this fixture's own process can reach this cleanup path.
                    $p.Kill()
                    [void]$p.WaitForExit(5000)
                }
            } finally { $p.Dispose() }
        }
    }
}

function Start-SctUnreadApp([string]$Exe) {
    Start-Process -FilePath $Exe -ArgumentList '--show-monitor' -WorkingDirectory (Split-Path -Parent $Exe) -WindowStyle Hidden -PassThru
}

function Wait-SctRemainingUnread([Diagnostics.Process]$Process, [switch]$AfterScan) {
    [void](Wait-SctUi 'only SCT-Beta remains unread' {
        $main = Get-SctMonitorWindow $Process
        if ($main -and (Get-SctUiLabel $main '未読 1 件') -and (Get-SctUiLabel $main 'SCT-Beta を既読にする') -and
            !(Get-SctUiLabel $main 'SCT-Alpha を既読にする') -and
            (!$AfterScan -or (Get-SctUiLabel $main '最終巡回 ' -Prefix))) { $true }
    })
}

function Get-SctMonitorWindow([Diagnostics.Process]$Process) {
    $Process.Refresh()
    if ($Process.HasExited) { throw "The GUI fixture exited early (exit code $($Process.ExitCode))." }
    $handle = $Process.MainWindowHandle
    if ($handle -ne [IntPtr]::Zero -and $Process.MainWindowTitle.StartsWith('かわったよ')) {
        return [Windows.Automation.AutomationElement]::FromHandle($handle)
    }
}

function Get-SctUiLabel($Window, [string]$Name, [switch]$Prefix) {
    $property = [Windows.Automation.AutomationElement]::NameProperty
    $scope = [Windows.Automation.TreeScope]::Descendants
    if (!$Prefix) { return $Window.FindFirst($scope, [Windows.Automation.PropertyCondition]::new($property, $Name)) }
    return $Window.FindAll($scope, [Windows.Automation.Condition]::TrueCondition) |
        Where-Object { $_.Current.Name.StartsWith($Name, [StringComparison]::Ordinal) } | Select-Object -First 1
}

function Invoke-SctUiButton($Window, [string]$Name) {
    $element = Get-SctUiLabel $Window $Name
    $pattern = $null
    if (!$element -or !$element.TryGetCurrentPattern([Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) {
        throw "The required UI button could not be invoked: $Name."
    }
    $pattern.Invoke()
}

function Wait-SctUi([string]$Description, [scriptblock]$Condition) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    do {
        try { $value = & $Condition }
        catch [Windows.Automation.ElementNotAvailableException] { $value = $null }
        if ($value) { return $value }
        Start-Sleep -Milliseconds 150
    } while ($timer.Elapsed.TotalSeconds -lt 15)
    throw "Timed out waiting for $Description."
}
