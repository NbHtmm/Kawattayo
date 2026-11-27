using System.Runtime.InteropServices;

namespace ShareFileWatch;

internal enum WindowRequest : byte { Monitor = 1, Collector = 2 }

internal static class WindowActivation
{
    // 他のデータやコマンドは受け付けず、2種類の画面表示だけを既存ウィンドウへ通知する。
    private const string WindowProperty = "Kawattayo.Activation.2040A88F";
    internal static readonly uint Message = RegisterWindowMessage(WindowProperty);
    private static readonly nint Marker = 1;

    internal static WindowRequest FromArguments(string[] args) =>
        args is ["--show-collector"] ? WindowRequest.Collector : WindowRequest.Monitor;

    internal static bool Register(nint handle) => handle != 0 && SetProp(handle, WindowProperty, Marker);
    internal static void Unregister(nint handle) { if (handle != 0) RemoveProp(handle, WindowProperty); }

    internal static bool Send(WindowRequest request)
    {
        if (Message == 0) return false;
        // 最初の起動直後は、ミューテックスを取得済みでもまだ HWND がないことがある。
        var until = Environment.TickCount64 + 1800;
        do
        {
            nint found = 0;
            EnumWindows((handle, _) =>
            {
                if (GetProp(handle, WindowProperty) != Marker) return true;
                found = handle;
                return false;
            }, 0);
            if (found != 0)
            {
                GetWindowThreadProcessId(found, out uint processId);
                if (processId != 0) AllowSetForegroundWindow(processId);
                if (PostMessage(found, Message, (nint)request, 0)) return true;
            }
            Thread.Sleep(60);
        } while (Environment.TickCount64 < until);
        return false;
    }

    private delegate bool EnumerateWindow(nint window, nint argument);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumerateWindow callback, nint argument);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(uint processId);
    [DllImport("user32.dll", EntryPoint = "SetPropW", CharSet = CharSet.Unicode)]
    private static extern bool SetProp(nint window, string name, nint value);
    [DllImport("user32.dll", EntryPoint = "GetPropW", CharSet = CharSet.Unicode)]
    private static extern nint GetProp(nint window, string name);
    [DllImport("user32.dll", EntryPoint = "RemovePropW", CharSet = CharSet.Unicode)]
    private static extern nint RemoveProp(nint window, string name);
    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll", EntryPoint = "PostMessageW", CharSet = CharSet.Unicode)]
    private static extern bool PostMessage(nint window, uint message, nint word, nint parameter);
}
