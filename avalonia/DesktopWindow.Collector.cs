namespace ShareFileWatch;

internal sealed partial class DesktopWindow
{
    private CollectorWindow? collector;

    internal void ActivateWindow(WindowRequest request)
    {
        if (__closing) return;
        if (request == WindowRequest.Collector) ShowCollector();
        else ShowFromTray();
    }

    internal void ShowCollector()
    {
        if (__closing) return;
        if (collector is null)
        {
            collector = new(new Checkpoints(__monitor.Store.StorageDirectory), ShowFromTray);
            collector.Closed += (_, _) =>
            {
                collector = null;
                if (!__closing && !IsVisible && !__native.IsVisible) ShowFromTray();
            };
            // 所有ウィンドウにしない。監視画面をトレイへ隠してもこちらは残す。
            collector.Show();
        }
        if (collector.WindowState == Avalonia.Controls.WindowState.Minimized)
            collector.WindowState = Avalonia.Controls.WindowState.Normal;
        collector.Activate();
    }
}
