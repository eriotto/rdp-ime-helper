using System.Runtime.InteropServices;
using RdpImeHelper.Logic;
using Windows.Win32;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace RdpImeHelper;

// SendInput ラッパー。ScanCode 指定のキーは KEYEVENTF_SCANCODE で送る。
// 送ったキーには目印（dwExtraInfo）を付け、フックで自分の送ったキーを見分けられるようにする。
internal static class InputSender
{
    /// <summary>自分が送ったキーの目印（"RIME"）。</summary>
    public const nuint Marker = 0x52494D45;

    private const int VkPacket = 0xE7;

    /// <summary>
    /// 物理キーを、受け取ったときと同じ VK・スキャンコードで送り直す（順序を保つため。<see cref="InjectionOrder"/>）。
    /// </summary>
    public static uint Reinject(KeyEvent e)
    {
        var flags = e.IsDown ? 0 : KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP;
        VIRTUAL_KEY vk = (VIRTUAL_KEY)e.Vk;
        if (e.Vk == VkPacket)
        {
            // Unicode 入力：scanCode に文字コードが入っている
            flags |= KEYBD_EVENT_FLAGS.KEYEVENTF_UNICODE;
            vk = 0;
        }
        else if (e.Extended)
        {
            flags |= KEYBD_EVENT_FLAGS.KEYEVENTF_EXTENDEDKEY;
        }

        Span<INPUT> inputs = stackalloc INPUT[1];
        inputs[0] = new INPUT
        {
            type = INPUT_TYPE.INPUT_KEYBOARD,
            Anonymous =
            {
                ki = new KEYBDINPUT
                {
                    wVk = vk,
                    wScan = (ushort)e.ScanCode,
                    dwFlags = flags,
                    dwExtraInfo = Marker,
                },
            },
        };

        return PInvoke.SendInput(inputs, Marshal.SizeOf<INPUT>());
    }

    public static uint Send(IReadOnlyList<SendKey> keys)
    {
        if (keys.Count == 0)
        {
            return 0;
        }

        Span<INPUT> inputs = stackalloc INPUT[keys.Count];
        for (int i = 0; i < keys.Count; i++)
        {
            var k = keys[i];
            KEYBD_EVENT_FLAGS flags = 0;
            if (k.ScanCode != 0)
            {
                flags |= KEYBD_EVENT_FLAGS.KEYEVENTF_SCANCODE;
            }

            if (k.Extended)
            {
                flags |= KEYBD_EVENT_FLAGS.KEYEVENTF_EXTENDEDKEY;
            }

            if (k.KeyUp)
            {
                flags |= KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP;
            }

            inputs[i] = new INPUT
            {
                type = INPUT_TYPE.INPUT_KEYBOARD,
                Anonymous =
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = k.ScanCode != 0 ? 0 : (VIRTUAL_KEY)k.Vk,
                        wScan = (ushort)k.ScanCode,
                        dwFlags = flags,
                        dwExtraInfo = Marker,
                    },
                },
            };
        }

        return PInvoke.SendInput(inputs, Marshal.SizeOf<INPUT>());
    }
}
