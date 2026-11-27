# These local cases exercise real file operations and visible GUI outcomes, not the monitor's internal state.
function Invoke-SctMonitorLifecycle([string]$Package, [string]$Work, [string]$Mode) {
    Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
    [void][IO.Directory]::CreateDirectory($Work)
    $install = Join-Path $Work 'app'
    [IO.Compression.ZipFile]::ExtractToDirectory($Package, $install)
    $exe = Join-Path $install 'かわったよ.exe'
    $root = Join-Path $Work 'SCT-Watch'
    [void][IO.Directory]::CreateDirectory($root)
    $file = Join-Path $root 'lifecycle.txt'
    [IO.File]::WriteAllText($file, 'initial')
    $folder = $Mode -ne 'FileLifecycle'
    $target = @{ Id = [guid]::NewGuid().ToString(); Path = $(if ($folder) { $root } else { $file });
        IsFolder = $folder; Recursive = $folder; RecursiveOverride = $folder }
    $settings = @{ Version = 1; Items = @($target); KeepInTray = $false; PollSeconds = 5;
        LowServerLoad = $false; NotifyDeleted = $true; WaitForStable = $false; ExcludedFilePatterns = @('~$*','*.tmp') }
    $settingsPath = Join-Path $install 'settings.json'
    if (Test-Path -LiteralPath $settingsPath) { throw 'Expected a clean fixture.' }
    [IO.File]::WriteAllText($settingsPath, ($settings | ConvertTo-Json -Depth 5))
    $evidence = Join-Path $Work 'observations.txt'
    $p = Start-SctUnreadApp $exe
    try {
        $view = Wait-SctUi 'completed clean baseline' {
            $main = Get-SctMonitorWindow $p
            if ($main -and (Get-SctUiLabel $main '未読 0 件') -and (Get-SctUiLabel $main '最終巡回 ' -Prefix)) { $main }
        }
        switch ($Mode) {
            'FolderOffline' { Test-SctLocalFolderOffline $p $view $root $file $evidence }
            'Exclusions' { Test-SctFolderExclusions $p $view $root $evidence }
            'History' { Test-SctHistory $p $view $root $evidence }
            'FileLifecycle' { Test-SctFileLifecycle $p $view $file $evidence }
            default { throw "Unknown GUI scenario: $Mode." }
        }
    } finally {
        try {
            if (!$p.HasExited -and !($p.CloseMainWindow() -and $p.WaitForExit(5000))) {
                $p.Kill() # Only the fixture process created above.
                [void]$p.WaitForExit(5000)
            }
        } finally { $p.Dispose() }
    }
}

function Test-SctFileLifecycle($Process, $View, [string]$File, [string]$Evidence) {
    [IO.File]::WriteAllText($File, 'a different size')
    $view = Wait-SctFileEvent $Process $View '更新'
    [IO.File]::Delete($File)
    $view = Wait-SctFileEvent $Process $view '消失'
    [IO.File]::WriteAllText($File, 'recreated')
    $view = Wait-SctFileEvent $Process $view '出現'

    # Verify the next complete poll happened; an immediate UI read would miss duplicate events.
    $prior = (Get-SctUiLabel $view '最終巡回 ' -Prefix).Current.Name
    Invoke-SctUiButton $view '↻ 更新'
    $view = Wait-SctUi 'another complete scan' {
        $main = Get-SctMonitorWindow $Process
        $status = if ($main) { Get-SctUiLabel $main '最終巡回 ' -Prefix }
        if ($status -and $status.Current.Name -ne $prior) { $main }
    }
    if (!(Get-SctUiLabel $view '未読 0 件')) { throw 'An unchanged file must not create a duplicate unread event.' }

    Invoke-SctUiButton $view '履歴'
    $history = Wait-SctUi 'history window' {
        Get-SctOwnedWindow -ProcessId $Process.Id -Title 'かわったよ | 変更履歴'
    }
    foreach ($eventName in '更新','消失','出現') {
        $property = [Windows.Automation.AutomationElement]::NameProperty
        $scope = [Windows.Automation.TreeScope]::Descendants
        $hits = @($history.FindAll($scope, [Windows.Automation.PropertyCondition]::new($property, $eventName)))
        if ($hits.Count -ne 1) { throw "Expected one history event of kind ${eventName}, got $($hits.Count)." }
    }
    [IO.File]::WriteAllText($Evidence, 'PASS: one update, one deletion, one reappearance; unchanged scan did not add unread.')
}

function Wait-SctFileEvent($Process, $View, [string]$Kind) {
    Invoke-SctUiButton $View '↻ 更新'
    $updated = Wait-SctUi "unread file event: $Kind" {
        $main = Get-SctMonitorWindow $Process
        if ($main -and (Get-SctUiLabel $main '未読 1 件') -and (Get-SctUiLabel $main $Kind) -and
            (Get-SctUiLabel $main 'lifecycle.txt を既読にする')) { $main }
    }
    Invoke-SctUiButton $updated 'lifecycle.txt を既読にする'
    Wait-SctUi "file event acknowledged: $Kind" {
        $main = Get-SctMonitorWindow $Process
        if ($main -and (Get-SctUiLabel $main '未読 0 件')) { $main }
    }
}

function Test-SctLocalFolderOffline($Process, $View, [string]$Root, [string]$File, [string]$Evidence) {
    [IO.Directory]::Move($Root, $Root + '.offline')
    Invoke-SctUiButton $View '↻ 更新'
    $view = Wait-SctUi 'local folder reports failure without invented deletion' {
        $main = Get-SctMonitorWindow $Process
        if ($main -and (Get-SctUiLabel $main '監視失敗 1件' -Prefix) -and (Get-SctUiLabel $main '未読 0 件')) { $main }
    }
    [IO.Directory]::Move($Root + '.offline', $Root)
    [IO.File]::WriteAllText($File, 'recovered and changed')
    Invoke-SctUiButton $view '↻ 更新'
    $view = Wait-SctUi 'recovery has one unread and no scan failure' {
        $main = Get-SctMonitorWindow $Process
        if ($main -and (Get-SctUiLabel $main '未読 1 件') -and !(Get-SctUiLabel $main '監視失敗 1件' -Prefix)) { $main }
    }
    Invoke-SctUiButton $view 'すべて展開'
    [void](Wait-SctUi 'recovery shows the changed file as an update' {
        $main = Get-SctMonitorWindow $Process
        if ($main -and (Get-SctUiLabel $main 'lifecycle.txt を既読にする') -and (Get-SctUiLabel $main '更新')) { $true }
    })
    [IO.File]::WriteAllText($Evidence, 'PASS: locally unavailable folder produced no deletion; restored folder showed one real update.')
}

# Owned windows may not be listed as direct children of the desktop's UIA tree.
function Get-SctOwnedWindow([int]$ProcessId,[string]$Title,[switch]$Prefix) {
    if (!('SctOwnedNative' -as [type])) {
        Add-Type -TypeDefinition @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class SctOwnedNative
{
    private delegate bool Callback(IntPtr window, IntPtr ignored);
    [DllImport("user32.dll")] private static extern bool EnumWindows(Callback callback, IntPtr ignored);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out int processId);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder name, int size);

    public static IntPtr Find(int pid, string title, bool prefix)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows(delegate(IntPtr window, IntPtr unused) {
            int id;
            GetWindowThreadProcessId(window, out id);
            if (id != pid || !IsWindowVisible(window)) return true;
            var name = new StringBuilder(512);
            GetWindowText(window, name, name.Capacity);
            var text = name.ToString();
            if (!(prefix ? text.StartsWith(title, StringComparison.Ordinal) : string.Equals(text, title, StringComparison.Ordinal))) return true;
            found = window;
            return false;
        }, IntPtr.Zero);
        return found;
    }
}
"@
    }
    $handle = [SctOwnedNative]::Find($ProcessId,$Title,$Prefix.IsPresent)
    if ($handle -ne [IntPtr]::Zero) { [Windows.Automation.AutomationElement]::FromHandle($handle) }
}

function Test-SctFolderExclusions($Process, $View, [string]$Root, [string]$Evidence) {
    $files = @('~$Draft.docx','scratch.TMP','reported.txt')
    foreach ($file in $files) { [IO.File]::WriteAllText((Join-Path $Root $file), 'test input') }
    Invoke-SctUiButton $View '↻ 更新'
    $view = Wait-SctUi 'one unread update under the watched folder' {
        $main = Get-SctMonitorWindow $Process
        if ($main -and (Get-SctUiLabel $main '未読 1 件')) { $main }
    }
    Invoke-SctUiButton $view 'すべて展開'
    $view = Wait-SctUi 'reported file in expanded unread tree' {
        $main = Get-SctMonitorWindow $Process
        if ($main -and (Get-SctUiLabel $main 'reported.txt を既読にする')) { $main }
    }
    foreach ($file in $files | Select-Object -First 2) {
        if ((Get-SctUiLabel $view $file) -or (Get-SctUiLabel $view "$file を既読にする")) {
            throw "Excluded file appeared as unread: $file."
        }
        if (![IO.File]::Exists((Join-Path $Root $file))) { throw "Excluded file was modified: $file." }
    }
    Invoke-SctUiButton $view '履歴'
    $history = Wait-SctUi 'history shows the non-excluded report' {
        $window = Get-SctOwnedWindow -ProcessId $Process.Id -Title 'かわったよ | 変更履歴'
        if ($window -and (Get-SctUiLabel $window (Join-Path $Root 'reported.txt'))) { $window }
    }
    foreach ($file in $files | Select-Object -First 2) {
        if (Get-SctUiLabel $history (Join-Path $Root $file)) { throw "Excluded file appeared in history: $file." }
    }
    [IO.File]::WriteAllText($Evidence, 'PASS: the ordinary file appears in unread and history; Office and mixed-case TMP files appear in neither and remain on disk.')
}

function Invoke-SctExistingInstance([string]$Package,[string]$Work) {
    Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
    [IO.Compression.ZipFile]::ExtractToDirectory($Package, $Work)
    $exe = Join-Path $Work 'かわったよ.exe'
    $p = Start-Process -FilePath $exe -ArgumentList '--show-collector' -WorkingDirectory $Work -WindowStyle Hidden -PassThru
    try {
        $collectorTitle = 'かわったよ · 差分収集'
        $collector = Wait-SctUi 'existing collector' { Get-SctOwnedWindow $p.Id $collectorTitle }
        $collectorHandle = $collector.Current.NativeWindowHandle
        $property = [Windows.Automation.AutomationElement]::ControlTypeProperty
        $edit = $collector.FindFirst([Windows.Automation.TreeScope]::Descendants,
            [Windows.Automation.PropertyCondition]::new($property, [Windows.Automation.ControlType]::Edit))
        $draft = 'SCT draft, not saved'
        $valuePattern = $null
        if (!$edit -or !$edit.TryGetCurrentPattern([Windows.Automation.ValuePattern]::Pattern,[ref]$valuePattern)) {
            throw 'The collector draft field is not editable.'
        }
        $valuePattern.SetValue($draft)
        $request = Start-Process -FilePath $exe -ArgumentList '--show-monitor' -WorkingDirectory $Work -WindowStyle Hidden -PassThru
        try {
            if (!$request.WaitForExit(7000) -or $request.ExitCode -ne 0) { throw 'Secondary launcher did not route and exit.' }
        } finally {
            if (!$request.HasExited) { $request.Kill(); [void]$request.WaitForExit(3000) }
            $request.Dispose()
        }
        $monitor = Wait-SctUi 'monitor in original process' { Get-SctOwnedWindow $p.Id 'かわったよ v' -Prefix }
        $after = Get-SctOwnedWindow $p.Id $collectorTitle
        if (!$after -or $after.Current.NativeWindowHandle -ne $collectorHandle -or
            $after.Current.IsOffscreen -or $monitor.Current.ProcessId -ne $p.Id -or
            $valuePattern.Current.Value -cne $draft) { throw 'The request changed or hid the original collector or its draft.' }
        [IO.File]::WriteAllText((Join-Path $Work 'observations.txt'),
            'PASS: the second launcher exited; original process has both windows; collector handle and draft are unchanged.')
    } finally {
        if (!$p.HasExited) { $p.Kill(); [void]$p.WaitForExit(5000) } # Only the isolated fixture instance.
        $p.Dispose()
    }
}
