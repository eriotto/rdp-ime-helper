using System.Runtime.InteropServices;
using RdpImeHelper.Logic;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Accessibility;
using Windows.Win32.UI.WindowsAndMessaging;

namespace RdpImeHelper;

// WH_KEYBOARD_LL / WH_MOUSE_LL の登録・解除と、KeyProcessor の出力の実行。
// メッセージループを持つ UI スレッドで生成すること（コールバックもこのスレッドで呼ばれる）。
// コールバックは軽く保つ：SendInput 以外の処理（IME 操作・ログ書き込み）は UI スレッド / ログスレッドへ回す。
internal sealed class KeyboardHook : IDisposable
{
    private readonly KeyProcessor _processor = new();
    private readonly InjectionOrder _order = new();
    private readonly LayoutMonitor _layout;
    private readonly SynchronizationContext _uiContext;
    private readonly HOOKPROC _keyboardProc;
    private readonly HOOKPROC _mouseProc;
    private readonly WINEVENTPROC _desktopSwitchProc;
    private UnhookWinEventSafeHandle? _desktopSwitchHook;
    private UnhookWindowsHookExSafeHandle? _keyboardHook;
    private UnhookWindowsHookExSafeHandle? _mouseHook;
    private volatile bool _forceConversion;

    /// <summary>診断：Shift を送ったあと、前面スレッドのキー状態をログに出す。</summary>
    public bool DiagnoseKeyState { get; set; }

    public KeyboardHook(LayoutMonitor layout)
    {
        _layout = layout;
        _uiContext = SynchronizationContext.Current
            ?? throw new InvalidOperationException("UI スレッドで生成してください。");
        _keyboardProc = KeyboardProc;
        _mouseProc = MouseProc;
        _desktopSwitchProc = DesktopSwitchProc;
    }

    /// <summary>ローカルでも強制的に US→JIS 変換を有効にする（テスト用）。</summary>
    public bool ForceConversion
    {
        get => _forceConversion;
        set => _forceConversion = value;
    }

    public bool IsConversionActive => _forceConversion || _layout.IsConversionConditionMet;

    public void Install()
    {
        using var module = PInvoke.GetModuleHandle((string?)null);
        _keyboardHook = PInvoke.SetWindowsHookEx(WINDOWS_HOOK_ID.WH_KEYBOARD_LL, _keyboardProc, module, 0);
        if (_keyboardHook.IsInvalid)
        {
            throw new InvalidOperationException($"WH_KEYBOARD_LL の登録に失敗しました（{Marshal.GetLastWin32Error()}）。");
        }

        _mouseHook = PInvoke.SetWindowsHookEx(WINDOWS_HOOK_ID.WH_MOUSE_LL, _mouseProc, module, 0);
        if (_mouseHook.IsInvalid)
        {
            throw new InvalidOperationException($"WH_MOUSE_LL の登録に失敗しました（{Marshal.GetLastWin32Error()}）。");
        }

        // セキュアデスクトップ（Ctrl+Alt+Del、UAC、ロック画面）中の key-up はフックに届かないため、切替時に状態を戻す
        _desktopSwitchHook = PInvoke.SetWinEventHook(
            PInvoke.EVENT_SYSTEM_DESKTOPSWITCH,
            PInvoke.EVENT_SYSTEM_DESKTOPSWITCH,
            null,
            _desktopSwitchProc,
            0,
            0,
            PInvoke.WINEVENT_OUTOFCONTEXT);
        if (_desktopSwitchHook.IsInvalid)
        {
            Logger.Log("デスクトップ切替の監視を登録できませんでした");
        }

        Logger.Log("フック登録");
    }

    /// <summary>追跡中のキー状態を戻す。UI スレッド（フックと同じスレッド）から呼ぶこと。</summary>
    public void ResetState(string reason, bool releaseModifiers = false) =>
        Execute(_processor.Reset(reason, releaseModifiers));

    public void Dispose()
    {
        _keyboardHook?.Dispose();
        _mouseHook?.Dispose();
        _desktopSwitchHook?.Dispose();
        _keyboardHook = null;
        _mouseHook = null;
        _desktopSwitchHook = null;
    }

    private void DesktopSwitchProc(HWINEVENTHOOK hook, uint @event, HWND hwnd, int idObject, int idChild, uint eventThread, uint eventTime)
    {
        try
        {
            ResetState("デスクトップ切替");
        }
        catch (Exception ex)
        {
            Logger.Log($"デスクトップ切替の処理で例外: {ex}");
        }
    }

    private unsafe LRESULT KeyboardProc(int code, WPARAM wParam, LPARAM lParam)
    {
        if (code >= 0)
        {
            try
            {
                var data = (KBDLLHOOKSTRUCT*)lParam.Value;
                if ((data->flags & KBDLLHOOKSTRUCT_FLAGS.LLKHF_INJECTED) != 0)
                {
                    // injected は処理しない。自分が送ったキーなら、キューから出たことだけ数える
                    if (data->dwExtraInfo == InputSender.Marker)
                    {
                        _order.OnOwnInjectedSeen();
                    }
                }
                else
                {
                    uint msg = (uint)wParam.Value;
                    var e = new KeyEvent(
                        (int)data->vkCode,
                        (int)data->scanCode,
                        (data->flags & KBDLLHOOKSTRUCT_FLAGS.LLKHF_EXTENDED) != 0,
                        msg == PInvoke.WM_KEYDOWN || msg == PInvoke.WM_SYSKEYDOWN,
                        data->time);

                    if (e.IsDown)
                    {
                        // キー押下ごとに前面ウィンドウの配列を判定（初出 HKL 以外はキャッシュ参照のみ）
                        _layout.OnKeyDown();
                    }

                    var result = _processor.Process(e, IsConversionActive);
                    Execute(result);
                    if (result.Suppress)
                    {
                        return new LRESULT(1);
                    }

                    // 自分の送ったキーがまだキューに残っているなら、素通しせず送り直して後ろに並べる
                    long now = Environment.TickCount64;
                    if (_order.ShouldReinject(now))
                    {
                        _order.OnSent((int)InputSender.Reinject(e), now);
                        return new LRESULT(1);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"キーボードフック例外: {ex}");
            }
        }

        return PInvoke.CallNextHookEx(null, code, wParam, lParam);
    }

    private LRESULT MouseProc(int code, WPARAM wParam, LPARAM lParam)
    {
        if (code >= 0)
        {
            try
            {
                uint msg = (uint)wParam.Value;
                if (msg is PInvoke.WM_LBUTTONDOWN or PInvoke.WM_RBUTTONDOWN
                    or PInvoke.WM_MBUTTONDOWN or PInvoke.WM_XBUTTONDOWN)
                {
                    Execute(_processor.OnMouseButtonDown());
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"マウスフック例外: {ex}");
            }
        }

        return PInvoke.CallNextHookEx(null, code, wParam, lParam);
    }

    private void Execute(KeyResult result)
    {
        if (result.Actions.Count == 0)
        {
            return;
        }

        List<SendKey>? keys = null;
        foreach (var action in result.Actions)
        {
            switch (action)
            {
                case SendKey k:
                    (keys ??= new()).Add(k);
                    break;
                case SetIme ime:
                    _uiContext.Post(static s => ImeController.SetOpen((bool)s!), ime.Open);
                    break;
                case Log log:
                    Logger.Log(log.Message);
                    break;
            }
        }

        if (keys != null)
        {
            uint sent = InputSender.Send(keys);
            _order.OnSent((int)sent, Environment.TickCount64);
            string described = string.Join(" ", keys.Select(Describe));
            Logger.Log($"SendInput {sent}/{keys.Count}: " + described);
            if (DiagnoseKeyState && keys.Any(k => k.Vk is KeyProcessor.VkLShift or KeyProcessor.VkRShift))
            {
                // 送ったキーが処理されたころに確認する（UI スレッドで。フック内では行わない）
                _uiContext.Post(_ => KeyStateProbe.LogLater(described), null);
            }
        }
    }

    private static string Describe(SendKey k) =>
        (k.ScanCode == 0 ? $"vk=0x{k.Vk:X2}"
            : InputSender.IsModifier(k.Vk) ? $"vk=0x{k.Vk:X2}(sc=0x{(k.Extended ? "E0" : "")}{k.ScanCode:X2})"
            : $"sc=0x{(k.Extended ? "E0" : "")}{k.ScanCode:X2}")
        + (k.KeyUp ? "↑" : "↓");
}
