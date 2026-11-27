using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;

namespace ShareFileWatch;

internal static class DesktopEntry
{
    [STAThread]
    public static int Main(string[] args)
    {
#if KAWATTAYO_SMOKE
        if (args.Length == 3 && args[0] == "--smoke")
        {
            DesktopApp.SmokeFolder = Path.GetFullPath(args[1]);
            DesktopApp.SmokeReport = Path.GetFullPath(args[2]);
            try { return Run([]); }
            catch (Exception error) { File.WriteAllText(DesktopApp.SmokeReport, error.ToString()); return 1; }
        }
#endif
        // 旧版と同じ保存先のデータを二つのプロセスが同時に操作しない。
        using var mutex = new Mutex(false, @"Local\ShareFileWatch-v1");
        bool owner;
        try { owner = mutex.WaitOne(0); }
        catch (AbandonedMutexException) { owner = true; }
        if (!owner)
        {
            WinDesktop.Message("かわったよはすでに起動しています。通知領域から終了してから開いてください。");
            return 1;
        }
        try
        {
            return Run(args);
        }
        catch (Exception error)
        {
            WinDesktop.Message(error.Message);
            return 1;
        }
        finally { mutex.ReleaseMutex(); }
    }
    private static int Run(string[] args) => AppBuilder.Configure<DesktopApp>().UseWin32().UseSkia().UseHarfBuzz()
        .StartWithClassicDesktopLifetime(args, ShutdownMode.OnMainWindowClose);
}

internal sealed class DesktopApp : Application
{
#if KAWATTAYO_SMOKE
    internal static string? SmokeFolder, SmokeReport;
#endif
    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
#if KAWATTAYO_SMOKE
            if (SmokeFolder is { } fixture && SmokeReport is { } report)
            {
                var monitor = new MonitorSession(StateStore.BesideExecutable(fixture));
                monitor.State.KeepInTray = false;
                var window = new DesktopWindow(monitor);
                window.AttachSmoke(report, fixture);
                desktop.MainWindow = window;
            }
            else
#endif
            desktop.MainWindow = new DesktopWindow(new MonitorSession(StateStore.BesideExecutable(AppContext.BaseDirectory)));
        }
        base.OnFrameworkInitializationCompleted();
    }
}
