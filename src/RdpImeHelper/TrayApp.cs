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
    private const int ReleaseRetryDelayMs = 2000;

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

        // ToolStripItem.Visible の getter はメニュー非表示中は常に false を返すので、判定結果は変数で持つ
        bool ignoreRemoteSet = LayoutMonitor.IsIgnoreRemoteKeyboardLayoutSet();
        var ignoreRemoteItem = new ToolStripMenuItem("IgnoreRemoteKeyboardLayout を設定する（管理者）")
        {
            Visible = !ignoreRemoteSet,
        };
        ignoreRemoteItem.Click += (_, _) => _layout.RequestSetIgnoreRemoteKeyboardLayout(set =>
        {
            ignoreRemoteItem.Visible = !set;
            Notify(
                set ? "IgnoreRemoteKeyboardLayout を設定しました。再接続（またはサインアウト）後に有効になります。"
                    : "IgnoreRemoteKeyboardLayout を設定できませんでした。",
                set ? ToolTipIcon.Info : ToolTipIcon.Warning);
        });
        Logger.Log($"IgnoreRemoteKeyboardLayout: {(ignoreRemoteSet ? "設定済み" : "未設定")}");

        var menu = new ContextMenuStrip();
        var diagItem = new ToolStripMenuItem("診断：Shift の状態をログに記録") { CheckOnClick = true };
        diagItem.CheckedChanged += (_, _) =>
        {
            _hook.DiagnoseKeyState = diagItem.Checked;
            Logger.Log($"診断（Shift の状態）: {(diagItem.Checked ? "ON" : "OFF")}");
        };

        menu.Items.Add(forceItem);
        menu.Items.Add(diagItem);
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
            // ロック・切断中の key-up はフックに届かないため、キー状態を戻す。
            // 再接続・ロック解除時は、システム側に残った Shift などの押しっぱなしも解除する
            bool release = reason is SessionSwitchReason.RemoteConnect
                or SessionSwitchReason.ConsoleConnect or SessionSwitchReason.SessionUnlock;
            _hook.ResetState($"セッション切替 {reason}", release);
            if (reason is SessionSwitchReason.RemoteConnect or SessionSwitchReason.ConsoleConnect)
            {
                _layout.OnConnected(reason.ToString());

                // 接続直後はまだロック画面（セキュアデスクトップ）で SendInput が失敗することがある（実機で 0/8）。
                // ロック解除のイベントが来ない接続もあるので、少し待ってからもう一度離しを送る
                var retry = new System.Windows.Forms.Timer { Interval = ReleaseRetryDelayMs };
                retry.Tick += (_, _) =>
                {
                    retry.Dispose();
                    _hook.ResetState($"接続後の再送 {reason}", releaseModifiers: true);
                };
                retry.Start();
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
        string conversion = _hook.IsConversionActive ? (_hook.ForceConversion ? "ON（強制）" : "ON")
            : _layout.Current is { IsJis: true } && LayoutMonitor.IsClientKeyboardJapanese ? "OFF（JISキーボード）"
            : "OFF";
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
