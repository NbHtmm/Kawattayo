using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace ShareFileWatch;

internal sealed class WinDesktop : IDisposable
{
    private const int CallbackMessage = 0x8000 + 35;
    private const uint IconFlags = 0x01 | 0x02 | 0x04;
    private const uint IconId = 1;
    private readonly Window window;
    private readonly Action show, scan, quit, collector;
    private readonly Action<string?> openNotification;
    private readonly Action<WindowRequest> activateWindow;
    private string? notifiedPath;
    private nint handle, icon;
    private uint taskbarCreated;
    private bool active;
    internal bool IsVisible => active;

    internal WinDesktop(Window window, Action show, Action scan, Action quit, Action collector, Action<string?> openNotification, Action<WindowRequest> activateWindow)
    {
        this.window = window;
        this.show = show;
        this.scan = scan;
        this.quit = quit;
        this.collector = collector;
        this.openNotification = openNotification;
        this.activateWindow = activateWindow;
    }

    internal void Attach()
    {
        if (handle != 0) return;
        handle = window.TryGetPlatformHandle()?.Handle ?? 0;
        if (handle == 0) return;
        taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        icon = LoadIcon(GetModuleHandle(null), (nint)32512);
        if (icon == 0) icon = LoadIcon(0, (nint)32512);
        Win32Properties.AddWndProcHookCallback(window, OnMessage);
        WindowActivation.Register(handle);
    }

    internal void SetVisible(bool visible)
    {
        if (handle == 0 || visible == active) return;
        var info = CreateInfo(IconFlags);
        if (visible) active = ShellNotifyIcon(0, ref info);
        else { ShellNotifyIcon(2, ref info); active = false; }
    }

    internal void Notify(IReadOnlyList<MonitorAlert> alerts)
    {
        if (!active) { MessageBeep(0x40); return; }
        var info = CreateInfo(0x10);
        info.Title = $"かわったよ ｜ {alerts.Count} 件の変更";
        var details = string.Join(Environment.NewLine, alerts.Take(3).Select(x => x.Label));
        info.Info = details.Length < 255 ? details : details[..252] + "…";
        info.InfoFlags = 1;
        if (ShellNotifyIcon(1, ref info)) notifiedPath = alerts.FirstOrDefault()?.Path;
    }

    private nint OnMessage(nint hwnd, uint msg, nint wParam, nint lParam, ref bool handled)
    {
        if (taskbarCreated != 0 && msg == taskbarCreated && active)
        {
            active = false;
            SetVisible(true);
            // Explorer再起動で通知領域へ戻せないときは、画面を復帰させる。
            if (!active) Avalonia.Threading.Dispatcher.UIThread.Post(show);
        }
        if (WindowActivation.Message != 0 && msg == WindowActivation.Message)
        {
            handled = true;
            if (wParam == (nint)WindowRequest.Monitor || wParam == (nint)WindowRequest.Collector)
                Avalonia.Threading.Dispatcher.UIThread.Post(() => activateWindow((WindowRequest)(byte)wParam));
            return 0;
        }
        if (msg != CallbackMessage) return 0;
        handled = true;
        switch ((int)lParam)
        {
            case 0x0203: show(); break; // ダブルクリックで画面を戻す
            case 0x0405: openNotification(notifiedPath); break; // NIN_BALLOONUSERCLICK
            case 0x0205: ShowMenu(); break;
        }
        return 0;
    }

    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == 0) return;
        try
        {
            AppendMenu(menu, 0, 1, "かわったよを開く");
            AppendMenu(menu, 0, 2, "今すぐ照会");
            AppendMenu(menu, 0, 4, "差分収集");
            AppendMenu(menu, 0, 3, "終了");
            GetCursorPos(out var point);
            SetForegroundWindow(handle);
            uint action = TrackPopupMenuEx(menu, 0x102, point.X, point.Y, handle, 0);
            PostMessage(handle, 0, 0, 0);
            if (action == 1) show();
            if (action == 2) scan();
            if (action == 3) quit();
            if (action == 4) collector();
        }
        finally { DestroyMenu(menu); }
    }

    private NotificationData CreateInfo(uint flags) => new()
    {
        Size = Marshal.SizeOf<NotificationData>(), Window = handle, Id = IconId, Flags = flags,
        Message = CallbackMessage, Icon = icon, Tip = "かわったよ", Info = "", Title = ""
    };

    public void Dispose()
    {
        SetVisible(false);
        WindowActivation.Unregister(handle);
        if (handle != 0) Win32Properties.RemoveWndProcHookCallback(window, OnMessage);
        handle = 0;
    }

    internal static void Message(string text, nint owner = 0) => MessageBox(owner, text, "かわったよ", 0x30);
    internal static bool Confirm(string text, nint owner) => MessageBox(owner, text, "かわったよ", 0x124) == 6;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotificationData
    {
        public int Size;
        public nint Window;
        public uint Id, Flags, Message;
        public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint TimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string Title;
        public uint InfoFlags;
        public Guid GuidItem;
        public nint BalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorPosition { public int X, Y; }
    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW")] private static extern bool ShellNotifyIcon(uint action, ref NotificationData data);
    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll", EntryPoint = "LoadIconW")] private static extern nint LoadIcon(nint instance, nint resource);
    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)] private static extern int MessageBox(nint owner, string text, string title, uint type);
    [DllImport("user32.dll")] private static extern bool MessageBeep(uint type);
    [DllImport("user32.dll")] private static extern nint CreatePopupMenu();
    [DllImport("user32.dll", EntryPoint = "AppendMenuW", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(nint menu, uint flags, nuint id, string text);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out CursorPosition position);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint handle);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint owner, nint rect);
    [DllImport("user32.dll", EntryPoint = "PostMessageW")] private static extern bool PostMessage(nint window, uint msg, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(nint menu);
}
