namespace ShareFileWatch;

/// @brief 起動引数を解釈し通常起動または自己テストを実行する。
internal static class Program
{
    /// @brief 起動引数を解釈し通常起動または自己テストを実行する。
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        // テスト引数とレポート先がそろった場合は通常の設定を開かず試験を実行する。
#if TEST_RUNNER
        if (args.Length == 1 && args[0] == "--perf") return MainWindow.MeasurePolling();
        if (args.Length == 1 && args[0] == "--sqlite-scale") return InventoryTests.Scale();
        if (args.Length == 2 && args[0] is "--self-test" or "--ui-test")
        {
            return SelfTest.Run(args[1], args[0] == "--ui-test");
        }
        return 2;
#else

        using var mutex = new Mutex(false, @"Local\ShareFileWatch-v1");
        bool owns;
        try
        {
            owns = mutex.WaitOne(0);
        }
        catch (AbandonedMutexException) { owns = true; }
        // 他のインスタンスが動作中なら、二重起動せず通知領域を案内する。
        if (!owns)
        {
            MessageBox.Show("すでに動作中です。タスクバー右下の通知領域を確認してください。", AppDefaults.AppName);
            return 0;
        }

        try
        {
            var store = StateStore.BesideExecutable(AppContext.BaseDirectory);
            using var window = new MainWindow(store);
            Application.Run(window);
            return 0;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, AppDefaults.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        finally { mutex.ReleaseMutex(); }
#endif
    }
}
