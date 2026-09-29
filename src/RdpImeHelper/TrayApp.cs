using System.Diagnostics;
using Windows.Win32;
using Windows.Win32.UI.WindowsAndMessaging;

namespace RdpImeHelper;

internal sealed class TrayApp : ApplicationContext
{
    private readonly NotifyIcon _notifyIcon;
    private readonly KeyboardHook _hook;

    public TrayApp()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("ログを開く", null, (_, _) => OpenLog());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("終了", null, (_, _) => ExitThread());

        _notifyIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = BuildTooltip(),
            ContextMenuStrip = menu,
            Visible = true,
        };

        _hook = new KeyboardHook();
        _hook.Install();
    }

    private static string BuildTooltip()
    {
        bool isRemote = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_REMOTESESSION) != 0;
        return $"RdpImeHelper（RDP：{(isRemote ? "○" : "×")}）";
    }

    private static void OpenLog()
    {
        if (File.Exists(Logger.LogPath))
        {
            Process.Start(new ProcessStartInfo(Logger.LogPath) { UseShellExecute = true });
        }
    }

    protected override void ExitThreadCore()
    {
        _hook.Dispose();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        base.ExitThreadCore();
    }
}
