function Initialize-SctDesktop([string]$Package, [string]$Directory) {
    $session = [Diagnostics.Process]::GetCurrentProcess().SessionId
    if (!$session -or ![Environment]::UserInteractive) { throw 'An interactive Windows session is required.' }
    # Never reset an existing installation or reuse the CLI fixture.
    if ([IO.Directory]::Exists($Directory)) { throw 'The desktop fixture must not already exist.' }
    [IO.Compression.ZipFile]::ExtractToDirectory($Package, $Directory)
    foreach ($name in @('settings.json','settings.json.bak','cache.json','cache.sqlite3','cache.sqlite3-wal','cache.sqlite3-shm')) {
        if (Test-Path -LiteralPath (Join-Path $Directory $name)) {
            throw "The supplied package contains application state: $name."
        }
    }
    $running = @(Get-Process -Name 'かわったよ','Kawattayo.Avalonia','ShareFileWatch' -ErrorAction SilentlyContinue |
        Where-Object { $_.SessionId -eq $session })
    if ($running.Count -gt 1) { throw 'Multiple Kawattayo candidates exist. Leave them untouched and close them manually.' }
    foreach ($process in $running) { Stop-SctDesktopInstance $process }

    # Do not launch if an unidentified process still owns the products single-instance lock.
    $mutex = $null
    if ([Threading.Mutex]::TryOpenExisting('Local\ShareFileWatch-v1', [ref]$mutex)) {
        $mutex.Dispose()
        throw 'The Kawattayo single-instance mutex is still present after shutdown.'
    }

}

function Stop-SctDesktopInstance([Diagnostics.Process]$Process) {
    $processId = $Process.Id
    if ($Process.HasExited) { return }
    if (!('SctDesktopNative' -as [type])) {
        Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
public static class SctDesktopNative
{
    private delegate bool WindowCallback(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out int pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetProp(IntPtr window, string name);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)] public struct Point { public int X; public int Y; }
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder text, int size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int size);

    private static bool IsLegacyWindow(IntPtr window)
    {
        var cls = new StringBuilder(256);
        var title = new StringBuilder(256);
        GetClassName(window, cls, cls.Capacity);
        GetWindowText(window, title, title.Capacity);
        return cls.ToString().StartsWith("WindowsForms10.Window.", StringComparison.Ordinal) &&
            Regex.IsMatch(title.ToString(), @"^かわったよ(?: v[0-9.]+)?\z");
    }

    public static IntPtr FindNativeMenu(int processId)
    {
        IntPtr match = IntPtr.Zero;
        EnumWindows(delegate(IntPtr window, IntPtr ignored) {
            int pid;
            GetWindowThreadProcessId(window, out pid);
            var cls = new StringBuilder(32);
            if (pid != processId || GetClassName(window, cls, cls.Capacity) == 0 || cls.ToString() != "#32768") return true;
            match = window;
            return false;
        }, IntPtr.Zero);
        return match;
    }

    public static IntPtr FindWindow(int processId, bool legacy)
    {
        IntPtr match = IntPtr.Zero;
        EnumWindows(delegate(IntPtr window, IntPtr ignored) {
            int pid;
            GetWindowThreadProcessId(window, out pid);
            if (pid != processId) return true;
            bool matches = legacy ? IsLegacyWindow(window) : GetProp(window, "Kawattayo.Activation.2040A88F") == (IntPtr)1;
            if (!matches) return true;
            match = window;
            return false;
        }, IntPtr.Zero);
        return match;
    }
}
"@
    }
    Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, WindowsBase
    $root = [Windows.Automation.AutomationElement]::RootElement
    $pidCondition = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty, $processId)
    $exitName = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty, '終了')
    $window = [SctDesktopNative]::FindWindow($processId, $false)

    $isAvalonia = $window -ne [IntPtr]::Zero
    if ($isAvalonia) {
        # Use the verified Avalonia window's own tray callback.
        if (![SctDesktopNative]::PostMessage($window, 0x8023, [IntPtr]1, [IntPtr]0x0205)) {
            throw "Could not request the tray menu (PID $processId)."
        }
    } else {
        if ($Process.HasExited) { return }
        # The legacy WinForms build has no activation marker. Require its known window and a unique app tray icon.
        $window = [SctDesktopNative]::FindWindow($processId, $true)
        if ($window -eq [IntPtr]::Zero) {
            throw "Could not identify the Kawattayo window (PID $processId). No forced termination was attempted."
        }
        # Without tray mode, an ordinary close is sufficient and avoids touching the taskbar.
        if ([SctDesktopNative]::PostMessage($window, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) -and $Process.WaitForExit(3000)) {
            Write-Host "Closed Kawattayo normally (PID $processId)."
            return
        }
        Open-SctLegacyTrayMenu $root
    }
    $timer = [Diagnostics.Stopwatch]::StartNew()
    do {
        if ($Process.HasExited) { return }
        if ($isAvalonia) {
            $popup = [SctDesktopNative]::FindNativeMenu($processId)
            $menus = if ($popup -ne [IntPtr]::Zero) { [Windows.Automation.AutomationElement]::FromHandle($popup) }
        } else {
            $menus = [Windows.Automation.AutomationElement]::RootElement.FindAll(
                [Windows.Automation.TreeScope]::Children, $pidCondition)
        }
        foreach ($menu in $menus) {
            $exitItem = $menu.FindFirst([Windows.Automation.TreeScope]::Descendants, $exitName)
            if ($null -eq $exitItem) { continue }
            Invoke-SctVerifiedClick $exitItem '終了' 0x0002 0x0004
            if (!$Process.WaitForExit(10000)) {
                throw "Kawattayo did not exit after selecting Exit (PID $processId). No forced termination was attempted."
            }
            Write-Host "Closed Kawattayo normally (PID $processId)."
            return
        }
        Start-Sleep -Milliseconds 100
    } while ($timer.Elapsed.TotalSeconds -lt 5)
    # Dismiss our menu if the desktop did not expose the Exit action.
    [void][SctDesktopNative]::PostMessage($window, 0x001F, [IntPtr]::Zero, [IntPtr]::Zero)
    throw "Could not invoke Kawattayo Exit (PID $processId). No forced termination was attempted."
}

function Open-SctLegacyTrayMenu($Root) {
    $scope = [Windows.Automation.TreeScope]::Descendants
    $class = [Windows.Automation.AutomationElement]::ClassNameProperty
    $name = [Windows.Automation.AutomationElement]::NameProperty
    $taskbar = $Root.FindFirst([Windows.Automation.TreeScope]::Children,
        [Windows.Automation.PropertyCondition]::new($class, 'Shell_TrayWnd'))
    if (!$taskbar) { throw 'The Windows taskbar is not accessible.' }
    $iconName = [Windows.Automation.PropertyCondition]::new($name, 'かわったよ')
    $icons = @($taskbar.FindAll($scope, $iconName))
    if ($icons.Count -eq 0) {
        $overflow = @($taskbar.FindAll($scope,
            [Windows.Automation.PropertyCondition]::new($class, 'SystemTray.NormalButton')) |
            Where-Object { $_.Current.Name -match '非表示のアイコン|隠れているインジケーター|hidden icons' })
        if ($overflow.Count -ne 1) { throw 'The tray overflow button could not be identified.' }
        $pattern = $null
        if (!$overflow[0].TryGetCurrentPattern([Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) {
            throw 'The tray overflow button cannot be invoked.'
        }
        $pattern.Invoke()
        $timer = [Diagnostics.Stopwatch]::StartNew()
        do {
            $icons = @($Root.FindAll($scope,
                [Windows.Automation.AndCondition]::new($iconName,
                    [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty,
                        $taskbar.Current.ProcessId))) | Where-Object { !$_.Current.IsOffscreen })
            if ($icons.Count -gt 0) { break }
            Start-Sleep -Milliseconds 100
        } while ($timer.Elapsed.TotalSeconds -lt 3)
    }
    if ($icons.Count -ne 1) { throw "Expected one Kawattayo tray icon, found $($icons.Count)." }
    Invoke-SctVerifiedClick $icons[0] 'かわったよ' 0x0008 0x0010
}

function Invoke-SctVerifiedClick($Element, [string]$Name, [uint32]$Down, [uint32]$Up) {
    $rect = $Element.Current.BoundingRectangle
    if (!$Element.Current.IsEnabled -or $rect.IsEmpty -or $rect.Width -le 0 -or $rect.Height -le 0) {
        throw "The expected control is not enabled and visible: $Name."
    }
    $point = [Windows.Point]::new([int]($rect.X + $rect.Width / 2), [int]($rect.Y + $rect.Height / 2))
    $ownerId = $Element.Current.ProcessId
    $hit = [Windows.Automation.AutomationElement]::FromPoint($point)
    while ($hit -and $hit.Current.ProcessId -eq $ownerId -and $hit.Current.Name -ne $Name) {
        $hit = [Windows.Automation.TreeWalker]::RawViewWalker.GetParent($hit)
    }
    if (!$hit -or $hit.Current.ProcessId -ne $ownerId -or $hit.Current.Name -ne $Name) {
        throw "The expected control is not at the current clickable point: $Name."
    }
    $cursor = New-Object 'SctDesktopNative+Point'
    if (![SctDesktopNative]::GetCursorPos([ref]$cursor)) { throw 'Could not read the cursor position.' }
    if (![SctDesktopNative]::SetCursorPos([int]$point.X, [int]$point.Y)) { throw 'Could not reach the control.' }
    try {
        [SctDesktopNative]::mouse_event($Down, 0, 0, 0, [UIntPtr]::Zero)
        [SctDesktopNative]::mouse_event($Up, 0, 0, 0, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds 120
    } finally {
        $current = New-Object 'SctDesktopNative+Point'
        if ([SctDesktopNative]::GetCursorPos([ref]$current) -and $current.X -eq $point.X -and $current.Y -eq $point.Y) {
            [void][SctDesktopNative]::SetCursorPos($cursor.X, $cursor.Y)
        }
    }
}
