namespace RdpImeHelper;

internal static class Program
{
    private const string MutexName = @"Local\RdpImeHelper.SingleInstance";

    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            return;
        }

        Logger.Start();
        Logger.Log("起動");
        try
        {
            ApplicationConfiguration.Initialize();
            Application.Run(new TrayApp());
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
