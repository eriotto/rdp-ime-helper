using Windows.Win32;

namespace RdpImeHelper;

// 診断用：前面ウィンドウのスレッドが認識しているキー状態を調べる（UI スレッドで呼ぶ）。
// 前面スレッドに一時的に AttachThreadInput して GetKeyboardState を読む。
internal static unsafe class KeyStateProbe
{
    private const int DelayMs = 150;

    public static void LogLater(string context)
    {
        var timer = new System.Windows.Forms.Timer { Interval = DelayMs };
        timer.Tick += (_, _) =>
        {
            timer.Dispose();
            Log(context);
        };
        timer.Start();
    }

    private static void Log(string context)
    {
        var foreground = PInvoke.GetForegroundWindow();
        uint target = foreground.IsNull ? 0 : PInvoke.GetWindowThreadProcessId(foreground, null);
        uint self = PInvoke.GetCurrentThreadId();
        bool attached = target != 0 && target != self && PInvoke.AttachThreadInput(self, target, true);

        var state = new byte[256];
        bool ok;
        fixed (byte* p = state)
        {
            ok = PInvoke.GetKeyboardState(p);
        }

        if (attached)
        {
            PInvoke.AttachThreadInput(self, target, false);
        }

        string Thread(int vk) => !ok ? "?" : (state[vk] & 0x80) != 0 ? "↓" : "↑";
        string Async(int vk) => (PInvoke.GetAsyncKeyState(vk) & 0x8000) != 0 ? "↓" : "↑";
        Logger.Log(
            $"診断: 前面スレッド Shift L{Thread(0xA0)} R{Thread(0xA1)} 全体{Thread(0x10)}"
            + $" ／ 非同期 L{Async(0xA0)} R{Async(0xA1)}"
            + $"{(attached ? "" : "（Attach できず、自スレッドの状態）")} ← {context}");
    }
}
