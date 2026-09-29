using System.Diagnostics;
using Microsoft.Win32;
using Windows.Win32;
using Windows.Win32.UI.WindowsAndMessaging;

namespace RdpImeHelper;

internal sealed class TrayApp : ApplicationContext
{
    private readonly NotifyIcon _notifyIcon;
    private readonly KeyboardHook _hook;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly SynchronizationContext _uiContext = SynchronizationContext.Current!;

    public TrayApp()
    {
        _hook = new KeyboardHook();

        var forceItem = new ToolStripMenuItem("ローカルでも強制有効（テスト用）") { CheckOnClick = true };
        forceItem.CheckedChanged += (_, _) =>
        {
            _hook.ForceConversion = forceItem.Checked;
            Logger.Log($"強制有効: {(forceItem.Checked ? "ON" : "OFF")}");
            UpdateTooltip();
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add(forceItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("ログを開く", null, (_, _) => OpenLog());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("終了", null, (_, _) => ExitThread());

        _notifyIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            ContextMenuStrip = menu,
            Visible = true,
        };

        _timer = new System.Windows.Forms.Timer { Interval = 1000 };
        _timer.Tick += (_, _) => UpdateTooltip();
        _timer.Start();
        UpdateTooltip();

        _hook.Install();
        SystemEvents.SessionSwitch += OnSessionSwitch;
    }

    // ロック・切断中の key-up はフックに届かないため、セッション切替時に状態を戻す
    // （SystemEvents は専用スレッドで発火するので UI スレッドへ回す）
    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        _uiContext.Post(_ => _hook.ResetState($"セッション切替 {e.Reason}"), null);
    }

    private void UpdateTooltip()
    {
        bool isRemote = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_REMOTESESSION) != 0;
        string layout = LayoutMonitor.IsJisLayout() ? "JIS" : "未判定";
        string conversion = _hook.IsConversionActive ? (_hook.ForceConversion ? "ON（強制）" : "ON") : "OFF";
        _notifyIcon.Text = $"RDP：{(isRemote ? "○" : "×")} ／ 配列：{layout} ／ 変換：{conversion}";
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
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        _timer.Dispose();
        _hook.Dispose();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        base.ExitThreadCore();
    }
}
