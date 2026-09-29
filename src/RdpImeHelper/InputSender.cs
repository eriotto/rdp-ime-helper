using System.Runtime.InteropServices;
using RdpImeHelper.Logic;
using Windows.Win32;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace RdpImeHelper;

// SendInput ラッパー。ScanCode 指定のキーは KEYEVENTF_SCANCODE で送る。
internal static class InputSender
{
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
                    },
                },
            };
        }

        return PInvoke.SendInput(inputs, Marshal.SizeOf<INPUT>());
    }
}
