using Windows.Win32;
using Windows.Win32.UI.WindowsAndMessaging;

namespace RdpImeHelper;

internal sealed class TrayApp : ApplicationContext
{
    private readonly NotifyIcon _notifyIcon;

    public TrayApp()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("終了", null, (_, _) => ExitThread());

        _notifyIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = BuildTooltip(),
            ContextMenuStrip = menu,
            Visible = true,
        };
    }

    private static string BuildTooltip()
    {
        bool isRemote = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_REMOTESESSION) != 0;
        return $"RdpImeHelper（RDP：{(isRemote ? "○" : "×")}）";
    }

    protected override void ExitThreadCore()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        base.ExitThreadCore();
    }
}
