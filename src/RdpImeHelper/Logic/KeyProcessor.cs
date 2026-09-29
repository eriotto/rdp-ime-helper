namespace RdpImeHelper.Logic;

/// <summary>キーボードフックから渡される1イベント（非injectedのみ）。</summary>
internal readonly record struct KeyEvent(int Vk, int ScanCode, bool Extended, bool IsDown, uint Time);

internal abstract record KeyAction;

/// <summary>SendInput で送るキー。ScanCode が 0 なら VK で、それ以外はスキャンコードで送る。</summary>
internal sealed record SendKey(int Vk, int ScanCode, bool Extended, bool KeyUp) : KeyAction;

/// <summary>IME のオン/オフ（UIスレッドで実行する）。</summary>
internal sealed record SetIme(bool Open) : KeyAction;

internal sealed record Log(string Message) : KeyAction;

internal readonly record struct KeyResult(bool Suppress, IReadOnlyList<KeyAction> Actions)
{
    public static readonly KeyResult PassThrough = new(false, Array.Empty<KeyAction>());
}

// 純粋ロジック。Win32 非依存（テストプロジェクトからソースリンクで参照される）。
// フックと同じスレッドからのみ呼ぶこと（スレッドセーフではない）。
internal sealed class KeyProcessor
{
    public const int VkLShift = 0xA0;
    public const int VkRShift = 0xA1;
    public const int VkLControl = 0xA2;
    public const int VkRControl = 0xA3;
    public const int VkLMenu = 0xA4;
    public const int VkRMenu = 0xA5;
    public const int VkLWin = 0x5B;
    public const int VkRWin = 0x5C;
    public const int VkDummy = 0xE8;
    public const int ScanAlt = 0x38;

    public const uint AltTapMaxMs = 500;

    private enum AltSide { None, Left, Right }

    // 物理状態（非injectedイベントから自前で追跡）
    private bool _lShift, _rShift, _lCtrl, _rCtrl, _lWin, _rWin, _lAlt, _rAlt;

    // 単押し候補
    private AltSide _pending;
    private uint _pendingDownTime;

    private bool AnyShiftCtrlWin => _lShift || _rShift || _lCtrl || _rCtrl || _lWin || _rWin;

    public KeyResult Process(KeyEvent e)
    {
        return e.Vk switch
        {
            VkLMenu => ProcessAlt(e, AltSide.Left),
            VkRMenu => ProcessAlt(e, AltSide.Right),
            _ => ProcessOther(e),
        };
    }

    /// <summary>WH_MOUSE_LL でボタン押下を検出したときに呼ぶ。</summary>
    public KeyResult OnMouseButtonDown()
    {
        if (_pending == AltSide.None)
        {
            return KeyResult.PassThrough;
        }

        var side = _pending;
        _pending = AltSide.None;
        return LogOnly($"Alt({side}): 取消（マウスボタン）");
    }

    private KeyResult ProcessAlt(KeyEvent e, AltSide side)
    {
        bool wasDown = side == AltSide.Left ? _lAlt : _rAlt;
        bool otherDown = side == AltSide.Left ? _rAlt : _lAlt;
        SetAlt(side, e.IsDown);

        if (e.IsDown)
        {
            if (wasDown)
            {
                // キーリピート。判定は継続
                return KeyResult.PassThrough;
            }

            if (otherDown)
            {
                var canceled = _pending;
                _pending = AltSide.None;
                return canceled == AltSide.None
                    ? KeyResult.PassThrough
                    : LogOnly($"Alt({canceled}): 取消（左右同時押し）");
            }

            if (AnyShiftCtrlWin)
            {
                _pending = AltSide.None;
                return LogOnly($"Alt({side}): 対象外（Ctrl/Shift/Win併用）");
            }

            _pending = side;
            _pendingDownTime = e.Time;
            return KeyResult.PassThrough;
        }

        // keyup
        if (_pending != side)
        {
            return KeyResult.PassThrough;
        }

        _pending = AltSide.None;
        uint elapsed = unchecked(e.Time - _pendingDownTime);
        if (elapsed > AltTapMaxMs)
        {
            return LogOnly($"Alt({side}): 対象外（長押し {elapsed}ms）");
        }

        bool right = side == AltSide.Right;
        // 元の Alt up を握りつぶし、ダミーキー → Alt up の順で送り直す（メニューバーのアクティブ化抑止）
        return new KeyResult(true, new KeyAction[]
        {
            new SendKey(VkDummy, 0, false, false),
            new SendKey(VkDummy, 0, false, true),
            new SendKey(right ? VkRMenu : VkLMenu, ScanAlt, right, true),
            new SetIme(right),
            new Log($"Alt({side}): 単押し {elapsed}ms → IME {(right ? "ON" : "OFF")}"),
        });
    }

    private KeyResult ProcessOther(KeyEvent e)
    {
        switch (e.Vk)
        {
            case VkLShift: _lShift = e.IsDown; break;
            case VkRShift: _rShift = e.IsDown; break;
            case VkLControl: _lCtrl = e.IsDown; break;
            case VkRControl: _rCtrl = e.IsDown; break;
            case VkLWin: _lWin = e.IsDown; break;
            case VkRWin: _rWin = e.IsDown; break;
        }

        if (_pending == AltSide.None)
        {
            return KeyResult.PassThrough;
        }

        var side = _pending;
        _pending = AltSide.None;
        return LogOnly($"Alt({side}): 取消（他キー vk=0x{e.Vk:X2} {(e.IsDown ? "down" : "up")}）");
    }

    private void SetAlt(AltSide side, bool down)
    {
        if (side == AltSide.Left)
        {
            _lAlt = down;
        }
        else
        {
            _rAlt = down;
        }
    }

    private static KeyResult LogOnly(string message) => new(false, new KeyAction[] { new Log(message) });
}
