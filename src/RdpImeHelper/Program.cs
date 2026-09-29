namespace RdpImeHelper;

internal static class Program
{
    private const string MutexName = @"Local\RdpImeHelper.SingleInstance";

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == LayoutMonitor.SetIgnoreRemoteKeyboardLayoutArg)
        {
            // トレイメニューから runas で昇格起動された：書き込んで終了（二重起動チェックより前）
            Logger.Start();
            int code = LayoutMonitor.WriteIgnoreRemoteKeyboardLayout();
            Logger.Stop();
            return code;
        }

        using var mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            return 0;
        }

        Logger.Start();
        Logger.Log("起動");
        try
        {
            ApplicationConfiguration.Initialize();
            // KeyboardHook が IME 操作を UI スレッドへ回すため、Control 生成前でも同期コンテキストを用意する
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            Application.Run(new TrayApp());
            return 0;
        }
        catch (Exception ex)
        {
            Logger.Log($"異常終了: {ex}");
            throw;
        }
        finally
        {
            Logger.Log("終了");
            Logger.Stop();
        }
    }
}
