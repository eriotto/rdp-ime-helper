using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Win32;
using RdpImeHelper.Logic;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace RdpImeHelper;

// 配列判定・是正。UI スレッド（フックと同じスレッド）でのみ使う。
internal sealed unsafe class LayoutMonitor
{
    public const string SetIgnoreRemoteKeyboardLayoutArg = "--set-ignore-remote-keyboard-layout";

    private const string KeyboardLayoutKey = @"SYSTEM\CurrentControlSet\Control\Keyboard Layout";
    private const string IgnoreRemoteKeyboardLayoutValue = "IgnoreRemoteKeyboardLayout";
    private const uint ScanAtKey = 0x1A;
    private const uint WmInputLangChangeRequest = 0x0050;
    private const int ConnectCheckDelayMs = 1500;
    private const int VerifyDelayMs = 500;

    private readonly LayoutDetector _detector = new();
    private readonly CorrectionPolicy _policy = new();
    private readonly SynchronizationContext _uiContext;
    private readonly Action<string, ToolTipIcon> _notify;

    public LayoutMonitor(Action<string, ToolTipIcon> notify)
    {
        _uiContext = SynchronizationContext.Current
            ?? throw new InvalidOperationException("UI スレッドで生成してください。");
        _notify = notify;
    }

    /// <summary>直近の判定結果（未判定なら null）。</summary>
    public LayoutJudgement? Current { get; private set; }

    public static bool IsRemoteSession =>
        PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_REMOTESESSION) != 0;

    /// <summary>US→JIS 変換の有効条件（RDP セッション かつ 配列が JIS）。</summary>
    public bool IsConversionConditionMet => IsRemoteSession && Current is { IsJis: true };

    /// <summary>
    /// フックの keydown ごとに呼ぶ。前面ウィンドウのスレッドの HKL を取得して判定する。
    /// 初出の HKL だけ MapVirtualKeyEx を呼ぶ（キャッシュ）。是正はここでは行わず UI スレッドへ回す。
    /// </summary>
    public void OnKeyDown()
    {
        var judgement = DetectForeground();
        if (Current == judgement)
        {
            return;
        }

        Current = judgement;
        Logger.Log($"配列判定: {judgement}");
        RequestIfNeeded(_policy.OnJudged(judgement, IsRemoteSession), judgement);
    }

    /// <summary>起動時と RDP の接続/再接続時に呼ぶ。是正の回数制限を戻し、少し待ってから判定する。</summary>
    public void OnConnected(string reason)
    {
        _policy.OnConnected();
        _detector.Clear();
        Current = null;
        Logger.Log($"接続（{reason}）: RDP={(IsRemoteSession ? "○" : "×")} 判定をやり直します");

        // 接続直後はクライアントの配列の同期が終わっていないことがあるので少し待つ
        RunLater(ConnectCheckDelayMs, () =>
        {
            var judgement = DetectForeground();
            Current = judgement;
            Logger.Log($"配列判定（接続後）: {judgement}");
            Handle(_policy.OnJudged(judgement, IsRemoteSession), judgement);
        });
    }

    private void RequestIfNeeded(CorrectionAction action, LayoutJudgement judgement)
    {
        if (action != CorrectionAction.None)
        {
            // フックのコールバック内では行わず、UI スレッドへ PostMessage で依頼する
            _uiContext.Post(_ => Handle(action, judgement), null);
        }
    }

    private void Handle(CorrectionAction action, LayoutJudgement judgement)
    {
        switch (action)
        {
            case CorrectionAction.CorrectLayout:
                CorrectLayout(judgement);
                break;
            case CorrectionAction.NotifyJapaneseNotJis:
                Logger.Log($"パターンB（日本語だが JIS として振る舞わない）: 通知のみ {judgement}");
                _notify(
                    "キーボード配列が JIS ではありません（日本語 / 101 配列）。IgnoreRemoteKeyboardLayout の設定と再接続を試してください。",
                    ToolTipIcon.Warning);
                break;
            case CorrectionAction.NotifyCorrectionFailed:
                _notify("キーボード配列を日本語（JIS）に戻せませんでした。", ToolTipIcon.Warning);
                break;
        }
    }

    // パターンA：LoadKeyboardLayout("00000411") + WM_INPUTLANGCHANGEREQUEST
    private void CorrectLayout(LayoutJudgement before)
    {
        var foreground = PInvoke.GetForegroundWindow();
        // SafeHandle 版は解放時に UnloadKeyboardLayout してしまうので、生の HKL を返す版を使う
        HKL hkl;
        fixed (char* klid = "00000411")
        {
            hkl = PInvoke.LoadKeyboardLayout(klid, ACTIVATE_KEYBOARD_LAYOUT_FLAGS.KLF_ACTIVATE);
        }

        if (hkl.IsNull)
        {
            Logger.Log($"是正（パターンA）: 失敗 LoadKeyboardLayout エラー {System.Runtime.InteropServices.Marshal.GetLastPInvokeError()} 是正前 {before}");
            Handle(CorrectionAction.NotifyCorrectionFailed, before);
            return;
        }

        bool posted = !foreground.IsNull && PInvoke.PostMessage(
            foreground, WmInputLangChangeRequest, default, new LPARAM((nint)hkl.Value));
        Logger.Log($"是正（パターンA）: LoadKeyboardLayout → HKL=0x{(ulong)(nint)hkl.Value:X8} "
            + $"WM_INPUTLANGCHANGEREQUEST hwnd=0x{(nint)foreground.Value:X} 送信={(posted ? "成功" : "失敗")} 是正前 {before}");

        // 反映を待ってから確認する
        RunLater(VerifyDelayMs, () =>
        {
            var after = DetectForeground();
            Current = after;
            var result = _policy.OnCorrectionResult(after);
            Logger.Log($"是正（パターンA）結果: {(result == CorrectionAction.None ? "成功" : "失敗")} 是正後 {after}");
            Handle(result, after);
        });
    }

    private LayoutJudgement DetectForeground()
    {
        var foreground = PInvoke.GetForegroundWindow();
        uint threadId = foreground.IsNull ? 0 : PInvoke.GetWindowThreadProcessId(foreground, null);
        var hkl = PInvoke.GetKeyboardLayout(threadId);
        return _detector.Detect((nint)hkl.Value, CharOfScan1A, out _);
    }

    // MapVirtualKeyEx でスキャンコード 0x1A の文字を得る（ToUnicodeEx はデッドキー状態を壊すので使わない）
    private static char CharOfScan1A(nint hkl)
    {
        var layout = new HKL((void*)hkl);
        uint vk = PInvoke.MapVirtualKeyEx(ScanAtKey, MAP_VIRTUAL_KEY_TYPE.MAPVK_VSC_TO_VK_EX, layout);
        if (vk == 0)
        {
            return '\0';
        }

        // 上位ビットはデッドキーのフラグ
        uint ch = PInvoke.MapVirtualKeyEx(vk, MAP_VIRTUAL_KEY_TYPE.MAPVK_VK_TO_CHAR, layout);
        return (char)(ch & 0xFFFF);
    }

    private static void RunLater(int delayMs, Action action)
    {
        var timer = new System.Windows.Forms.Timer { Interval = delayMs };
        timer.Tick += (_, _) =>
        {
            timer.Dispose();
            action();
        };
        timer.Start();
    }

    // ---- IgnoreRemoteKeyboardLayout ----

    public static bool IsIgnoreRemoteKeyboardLayoutSet()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(KeyboardLayoutKey);
            return key?.GetValue(IgnoreRemoteKeyboardLayoutValue) is int value && value == 1;
        }
        catch (Exception ex)
        {
            Logger.Log($"IgnoreRemoteKeyboardLayout: 読み取り失敗 {ex.Message}");
            return false;
        }
    }

    /// <summary>昇格起動された自身の中で呼ばれる。終了コード 0 で成功。</summary>
    public static int WriteIgnoreRemoteKeyboardLayout()
    {
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(KeyboardLayoutKey);
            key.SetValue(IgnoreRemoteKeyboardLayoutValue, 1, RegistryValueKind.DWord);
            Logger.Log("IgnoreRemoteKeyboardLayout: 書き込み成功");
            return 0;
        }
        catch (Exception ex)
        {
            Logger.Log($"IgnoreRemoteKeyboardLayout: 書き込み失敗 {ex.Message}");
            return 1;
        }
    }

    /// <summary>自身を runas で昇格起動して書き込む。完了後に UI スレッドで onDone(設定済みか) を呼ぶ。</summary>
    public void RequestSetIgnoreRemoteKeyboardLayout(Action<bool> onDone)
    {
        Process? process;
        try
        {
            process = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, SetIgnoreRemoteKeyboardLayoutArg)
            {
                UseShellExecute = true,
                Verb = "runas",
            });
        }
        catch (Win32Exception ex)
        {
            // 1223 = UAC でキャンセル
            Logger.Log($"IgnoreRemoteKeyboardLayout: 昇格起動できませんでした（{ex.NativeErrorCode}）");
            onDone(false);
            return;
        }

        if (process == null)
        {
            onDone(IsIgnoreRemoteKeyboardLayoutSet());
            return;
        }

        Task.Run(() =>
        {
            using (process)
            {
                process.WaitForExit();
                Logger.Log($"IgnoreRemoteKeyboardLayout: 昇格プロセス終了 コード {process.ExitCode}");
            }
        }).ContinueWith(_ => _uiContext.Post(_ => onDone(IsIgnoreRemoteKeyboardLayoutSet()), null));
    }
}
