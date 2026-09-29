using System.Runtime.InteropServices;
using RdpImeHelper.Logic;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace RdpImeHelper;

// WH_KEYBOARD_LL / WH_MOUSE_LL の登録・解除と、KeyProcessor の出力の実行。
// メッセージループを持つ UI スレッドで生成すること（コールバックもこのスレッドで呼ばれる）。
// コールバックは軽く保つ：SendInput 以外の処理（IME 操作・ログ書き込み）は UI スレッド / ログスレッドへ回す。
internal sealed class KeyboardHook : IDisposable
{
    private readonly KeyProcessor _processor = new();
    private readonly SynchronizationContext _uiContext;
    private readonly HOOKPROC _keyboardProc;
    private readonly HOOKPROC _mouseProc;
    private UnhookWindowsHookExSafeHandle? _keyboardHook;
    private UnhookWindowsHookExSafeHandle? _mouseHook;

    public KeyboardHook()
    {
        _uiContext = SynchronizationContext.Current
            ?? throw new InvalidOperationException("UI スレッドで生成してください。");
        _keyboardProc = KeyboardProc;
        _mouseProc = MouseProc;
    }

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

        Logger.Log("フック登録");
    }

    public void Dispose()
    {
        _keyboardHook?.Dispose();
        _mouseHook?.Dispose();
        _keyboardHook = null;
        _mouseHook = null;
    }

    private unsafe LRESULT KeyboardProc(int code, WPARAM wParam, LPARAM lParam)
    {
        if (code >= 0)
        {
            try
            {
                var data = (KBDLLHOOKSTRUCT*)lParam.Value;
                if ((data->flags & KBDLLHOOKSTRUCT_FLAGS.LLKHF_INJECTED) == 0)
                {
                    uint msg = (uint)wParam.Value;
                    var e = new KeyEvent(
                        (int)data->vkCode,
                        (int)data->scanCode,
                        (data->flags & KBDLLHOOKSTRUCT_FLAGS.LLKHF_EXTENDED) != 0,
                        msg == PInvoke.WM_KEYDOWN || msg == PInvoke.WM_SYSKEYDOWN,
                        data->time);

                    var result = _processor.Process(e);
                    Execute(result);
                    if (result.Suppress)
                    {
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
            Logger.Log($"SendInput {sent}/{keys.Count}: " + string.Join(" ", keys.Select(Describe)));
        }
    }

    private static string Describe(SendKey k) =>
        (k.ScanCode != 0 ? $"sc=0x{(k.Extended ? "E0" : "")}{k.ScanCode:X2}" : $"vk=0x{k.Vk:X2}")
        + (k.KeyUp ? "↑" : "↓");
}
