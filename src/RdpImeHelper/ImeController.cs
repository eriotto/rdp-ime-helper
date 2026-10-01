using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace RdpImeHelper;

// ImmGetDefaultIMEWnd + WM_IME_CONTROL で前面ウィンドウの IME を開閉する。UIスレッドから呼ぶこと。
internal static unsafe class ImeController
{
    private const uint WmImeControl = 0x283;
    private const nuint ImcGetOpenStatus = 0x5;
    private const nuint ImcSetOpenStatus = 0x6;
    private const uint TimeoutMs = 200;

    public static void SetOpen(bool open)
    {
        var target = GetFocusWindow();
        if (target.IsNull)
        {
            Logger.Log($"IME {(open ? "ON" : "OFF")}: 失敗（前面ウィンドウなし）");
            return;
        }

        var imeWnd = PInvoke.ImmGetDefaultIMEWnd(target);
        if (imeWnd.IsNull)
        {
            Logger.Log($"IME {(open ? "ON" : "OFF")}: 失敗（IMEウィンドウなし hwnd=0x{(nint)target.Value:X}）");
            return;
        }

        if (!Send(imeWnd, ImcSetOpenStatus, open ? 1 : 0, out _))
        {
            Logger.Log($"IME {(open ? "ON" : "OFF")}: 失敗（SetOpenStatus タイムアウト/エラー {Marshal.GetLastWin32Error()}）");
            return;
        }

        string verify = Send(imeWnd, ImcGetOpenStatus, 0, out nuint status)
            ? (status != 0) == open ? "確認OK" : $"確認NG（状態={status}）"
            : "確認不可";
        Logger.Log($"IME {(open ? "ON" : "OFF")}: 送信済み hwnd=0x{(nint)target.Value:X} ime=0x{(nint)imeWnd.Value:X} {verify}");
    }

    private static HWND GetFocusWindow()
    {
        var fg = PInvoke.GetForegroundWindow();
        if (fg.IsNull)
        {
            return fg;
        }

        uint threadId = PInvoke.GetWindowThreadProcessId(fg, null);

        var info = new GUITHREADINFO { cbSize = (uint)Marshal.SizeOf<GUITHREADINFO>() };
        if (PInvoke.GetGUIThreadInfo(threadId, ref info) && !info.hwndFocus.IsNull)
        {
            return info.hwndFocus;
        }

        return fg;
    }

    private static bool Send(HWND imeWnd, nuint command, nint value, out nuint result)
    {
        nuint r = 0;
        var ok = PInvoke.SendMessageTimeout(
            imeWnd,
            WmImeControl,
            new WPARAM(command),
            new LPARAM(value),
            SEND_MESSAGE_TIMEOUT_FLAGS.SMTO_ABORTIFHUNG,
            TimeoutMs,
            &r);
        result = r;
        return ok != 0;
    }
}
