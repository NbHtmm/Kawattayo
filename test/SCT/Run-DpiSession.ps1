
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Package,
    [switch]$DedicatedSession,
    [switch]$InspectOnly
)
$ErrorActionPreference = 'Stop'
if ($DedicatedSession.IsPresent -eq $InspectOnly.IsPresent) { throw 'Choose exactly one mode.' }
if (![Environment]::UserInteractive -or ![Diagnostics.Process]::GetCurrentProcess().SessionId) {
    throw 'A dedicated interactive Windows desktop is required.'
}
Add-Type -AssemblyName System.Windows.Forms
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class SctDpiProbe {
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out int processId);
    [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int L,T,R,B; }
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] private struct MonitorInfo {
        public int Size; public Rect Monitor,Work; public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=32)] public string Device;
    }
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window,uint flags);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor,ref MonitorInfo info);
    public static string ActualMonitor(IntPtr window) {
        var handle=MonitorFromWindow(window,2);
        var info=new MonitorInfo { Size=Marshal.SizeOf(typeof(MonitorInfo)),Device="" };
        return handle!=IntPtr.Zero && GetMonitorInfo(handle,ref info) ? info.Device : null;
    }
}
"@
$old = [SctDpiProbe]::SetThreadDpiAwarenessContext([IntPtr](-4))
if ($old -eq [IntPtr]::Zero) { throw 'Cannot get unscaled monitor coordinates in this Windows session.' }
try {
    $screens = @([Windows.Forms.Screen]::AllScreens | ForEach-Object {
        @{ name=$_.DeviceName; width=$_.Bounds.Width; height=$_.Bounds.Height; primary=$_.Primary }
    })
} finally { [void][SctDpiProbe]::SetThreadDpiAwarenessContext($old) }

$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$work = Join-Path (Join-Path $repo 'build') ('DPI-001-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($work)
$zip = (Resolve-Path -LiteralPath $Package).ProviderPath
$log = @{ id='SCT-DPI-001'; outcome='BLOCKED'; packageSha256=(Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
    atUtc=[DateTimeOffset]::UtcNow.ToString('o'); windows=[Environment]::OSVersion.VersionString
    screenLayout=$screens; observations=@(); error=$null }
$path = Join-Path $work 'environment-result.json'
if ($InspectOnly -or $screens.Count -lt 2 -or @($screens | Where-Object { $_.width -ge 3840 -and $_.height -ge 2160 }).Count -eq 0) {
    $log.outcome = if ($InspectOnly) { 'INSPECTED_ONLY' } else { 'BLOCKED' }
    $log.error = 'Real case requires two displays including 4K and different actual DPI.'
    [IO.File]::WriteAllText($path,($log | ConvertTo-Json -Depth 5))
    Write-Host "Display inventory only: $path"
    if (!$InspectOnly) { throw $log.error }
    return
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

$install = Join-Path $work 'product'
[IO.Compression.ZipFile]::ExtractToDirectory($zip,$install)
foreach ($stateName in 'settings.json','settings.json.bak','cache.json','cache.sqlite3','cache.sqlite3-wal','cache.sqlite3-shm') {
    if ([IO.File]::Exists((Join-Path $install $stateName))) { throw "ZIP contains application state: $stateName." }
}
$root = Join-Path $work 'ResizeFixture'
$nested = Join-Path $root 'folder-level'
[void][IO.Directory]::CreateDirectory($nested)
$file = Join-Path $nested ('record-' + ('sample-' * 12) + '.txt')
$leaf = [IO.Path]::GetFileName($file)
$configuration = @{ Version=1;KeepInTray=$false;LowServerLoad=$false;PollSeconds=5;WaitForStable=$false;TextSizePoints=17;
    Items=@(@{Id=[guid]::NewGuid().ToString();Path=$root;IsFolder=$true;Recursive=$true}) }
[IO.File]::WriteAllText((Join-Path $install 'settings.json'),($configuration | ConvertTo-Json -Depth 4))
$p = $null
try {
    $p = Start-SctUnreadApp (Join-Path $install 'かわったよ.exe')
    $view = Wait-SctUi 'baseline on one screen' {
        $window = Get-SctOwnedWindow $p.Id 'かわったよ v' -Prefix
        if ($window -and (Get-SctUiLabel $window '最終巡回 ' -Prefix)) { $window }
    }
    [IO.File]::WriteAllText($file,'DPI test contents')
    Invoke-SctUiButton $view '↻ 更新'
    [void](Wait-SctUi 'one folder change' { Get-SctUiLabel $view '未読 1 件' })
    foreach ($display in '4K display','other display with a different scale') {
        Read-Host "Move only the test window to the $display, narrow it, expand both folder levels using the visible buttons, then press Enter"
        [void](Wait-SctUi 'file remains reachable at narrow window width' { Get-SctUiLabel $view "$leaf を既読にする" })
        $handle = [IntPtr]$view.Current.NativeWindowHandle
        $owner = 0
        [void][SctDpiProbe]::GetWindowThreadProcessId($handle,[ref]$owner)
        if ($owner -ne $p.Id) { throw 'Fixture window no longer belongs to the test process.' }
        $dpi = [SctDpiProbe]::GetDpiForWindow($handle)
        if (!$dpi) { throw 'The real window DPI could not be measured.' }
        $bounds = $view.Current.BoundingRectangle
        $viewWidth = [int]$bounds.Width
        $device = [SctDpiProbe]::ActualMonitor($handle)
        $screen = @($screens | Where-Object { $_.name -ieq $device })
        if ($screen.Count -ne 1) { throw 'The fixture monitor is not in the inventory.' }
        if ($log.observations.Count -eq 0 -and ($screen[0].width -lt 3840 -or $screen[0].height -lt 2160)) {
            throw 'The first position was not on the expected 4K display.'
        }
        $log.observations += @{ display=$display; device=$device; dpi=$dpi; windowWidthPixels=$viewWidth }
        $clickCheck = Read-Host 'Confirm the actual pointer hits only the intended expander and filenames/controls remain accessible (YES or NO)'
        if ($clickCheck -cne 'YES') { $log.outcome='FAIL'; throw 'Pointer or display acceptance was not confirmed.' }
    }
    if ($log.observations[0].dpi -eq $log.observations[1].dpi -or $log.observations[0].device -ieq $log.observations[1].device) { throw 'The two positions did not have different DPI.' }
    $log.outcome = 'PASS'
} catch {
    $log.error = $_.Exception.Message
} finally {
    if ($p) { Stop-SctPreferenceFixture $p }
    [IO.File]::WriteAllText($path,($log | ConvertTo-Json -Depth 5))
    Write-Host "Evidence: $path ($($log.outcome))"
}
if ($log.outcome -ne 'PASS') { throw "$($log.outcome) : $($log.error)" }
