using System.Diagnostics;
using Microsoft.Win32;
using RdpImeHelper.Logic;

namespace RdpImeHelper;

internal sealed class TrayApp : ApplicationContext
{
    private readonly NotifyIcon _notifyIcon;
    private readonly KeyboardHook _hook;
    private readonly LayoutMonitor _layout;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly SynchronizationContext _uiContext = SynchronizationContext.Current!;

    public TrayApp()
    {
        _layout = new LayoutMonitor(Notify);
        _hook = new KeyboardHook(_layout);

        var forceItem = new ToolStripMenuItem("ローカルでも強制有効（テスト用）") { CheckOnClick = true };
        forceItem.CheckedChanged += (_, _) =>
        {
            _hook.ForceConversion = forceItem.Checked;
            Logger.Log($"強制有効: {(forceItem.Checked ? "ON" : "OFF")}");
            UpdateTooltip();
        };

        var ignoreRemoteItem = new ToolStripMenuItem("IgnoreRemoteKeyboardLayout を設定する（管理者）")
        {
            Visible = !LayoutMonitor.IsIgnoreRemoteKeyboardLayoutSet(),
        };
        ignoreRemoteItem.Click += (_, _) => _layout.RequestSetIgnoreRemoteKeyboardLayout(set =>
        {
            ignoreRemoteItem.Visible = !set;
            Notify(
                set ? "IgnoreRemoteKeyboardLayout を設定しました。再接続（またはサインアウト）後に有効になります。"
                    : "IgnoreRemoteKeyboardLayout を設定できませんでした。",
                set ? ToolTipIcon.Info : ToolTipIcon.Warning);
        });
        Logger.Log($"IgnoreRemoteKeyboardLayout: {(ignoreRemoteItem.Visible ? "未設定" : "設定済み")}");

        var menu = new ContextMenuStrip();
        menu.Items.Add(forceItem);
        menu.Items.Add(ignoreRemoteItem);
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
        // SystemEvents.SessionSwitch は WTSRegisterSessionNotification の通知（WM_WTSSESSION_CHANGE）
        SystemEvents.SessionSwitch += OnSessionSwitch;
        _layout.OnConnected("起動");
    }

    // SystemEvents は専用スレッドで発火するので UI スレッドへ回す
    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        var reason = e.Reason;
        _uiContext.Post(_ =>
        {
            // ロック・切断中の key-up はフックに届かないため、キー状態を戻す
            _hook.ResetState($"セッション切替 {reason}");
            if (reason is SessionSwitchReason.RemoteConnect or SessionSwitchReason.ConsoleConnect)
            {
                _layout.OnConnected(reason.ToString());
            }
        }, null);
    }

    private void Notify(string text, ToolTipIcon icon)
    {
        _notifyIcon.ShowBalloonTip(5000, "RdpImeHelper", text, icon);
    }

    private void UpdateTooltip()
    {
        bool isRemote = LayoutMonitor.IsRemoteSession;
        string layout = _layout.Current switch
        {
            null => "未判定",
            { IsJis: true } => "JIS",
            { Problem: LayoutProblem.NonJapanese } => "日本語以外",
            _ => "日本語(非JIS)",
        };
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
