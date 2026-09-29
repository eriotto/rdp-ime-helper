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
    public const int VkPacket = 0xE7;
    public const int ScanAlt = 0x38;
    public const int ScanLShift = 0x2A;
    public const int ScanRShift = 0x36;
    public const int ScanCapsLock = 0x3A;

    public const uint AltTapMaxMs = 500;

    private enum AltSide { None, Left, Right }

    /// <summary>JIS 側で送るキー。Shift は送信時の Shift 状態。</summary>
    internal readonly record struct JisKey(int ScanCode, bool Shift, string Label);

    // US→JIS 変換表。キーは US 入力の（スキャンコード, Shift 押下）。
    // ここに無い組み合わせは US と JIS で同じ文字になるので素通しする。
    internal static readonly IReadOnlyDictionary<(int ScanCode, bool Shift), JisKey> UsToJis =
        new Dictionary<(int, bool), JisKey>
        {
            [(0x03, true)] = new(0x1A, false, "@"),
            [(0x07, true)] = new(0x0D, false, "^"),
            [(0x08, true)] = new(0x07, true, "&"),
            [(0x09, true)] = new(0x28, true, "*"),
            [(0x0A, true)] = new(0x09, true, "("),
            [(0x0B, true)] = new(0x0A, true, ")"),
            [(0x0C, true)] = new(0x73, true, "_"),
            [(0x0D, false)] = new(0x0C, true, "="),
            [(0x0D, true)] = new(0x27, true, "+"),
            [(0x1A, false)] = new(0x1B, false, "["),
            [(0x1A, true)] = new(0x1B, true, "{"),
            [(0x1B, false)] = new(0x2B, false, "]"),
            [(0x1B, true)] = new(0x2B, true, "}"),
            [(0x2B, false)] = new(0x73, false, "\\"),
            [(0x2B, true)] = new(0x7D, true, "|"),
            [(0x27, true)] = new(0x28, false, ":"),
            [(0x28, false)] = new(0x08, true, "'"),
            [(0x28, true)] = new(0x03, true, "\""),
            // US の ` は JIS の半角/全角(0x29)と同位置。素通しすると IME が切り替わるので必ず横取りする
            [(0x29, false)] = new(0x1A, true, "`"),
            [(0x29, true)] = new(0x0D, true, "~"),
            // JIS の 0x3A は英数キー。Shift+英数 が CapsLock（挙動は実機で要確認）
            [(ScanCapsLock, false)] = new(ScanCapsLock, true, "CapsLock"),
        };

    private static readonly HashSet<int> ConvertibleScanCodes = UsToJis.Keys.Select(k => k.ScanCode).ToHashSet();

    // 物理状態（非injectedイベントから自前で追跡）
    private bool _lShift, _rShift, _lCtrl, _rCtrl, _lWin, _rWin, _lAlt, _rAlt;

    // 単押し候補
    private AltSide _pending;
    private uint _pendingDownTime;

    // 変換対象キーの押下状態。値は「押下を変換したか」（false なら元のキーの押下がシステムに渡っている）
    private readonly Dictionary<int, bool> _convertibleDown = new();

    private bool AnyShiftCtrlWin => _lShift || _rShift || _lCtrl || _rCtrl || _lWin || _rWin;

    private bool AnyCtrlAltWin => _lCtrl || _rCtrl || _lAlt || _rAlt || _lWin || _rWin;

    /// <param name="conversionEnabled">US→JIS 変換の有効条件（RDP かつ JIS 配列、または強制有効）の判定結果。</param>
    public KeyResult Process(KeyEvent e, bool conversionEnabled = false)
    {
        return e.Vk switch
        {
            VkLMenu => ProcessAlt(e, AltSide.Left),
            VkRMenu => ProcessAlt(e, AltSide.Right),
            _ => ProcessOther(e, conversionEnabled),
        };
    }

    /// <summary>
    /// 追跡している状態をすべて「離されている」に戻す。
    /// ロック画面・セキュアデスクトップ・RDP 切断中の key-up はフックに届かないため、
    /// セッション切替やデスクトップ切替のときに呼ぶ（古い Shift 状態で Shift を押し直す事故を防ぐ）。
    /// </summary>
    public KeyResult Reset(string reason)
    {
        _lShift = _rShift = _lCtrl = _rCtrl = _lWin = _rWin = _lAlt = _rAlt = false;
        _pending = AltSide.None;
        _convertibleDown.Clear();
        return LogOnly($"状態リセット（{reason}）");
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

    private KeyResult ProcessOther(KeyEvent e, bool conversionEnabled)
    {
        bool isModifier = true;
        switch (e.Vk)
        {
            case VkLShift: _lShift = e.IsDown; break;
            case VkRShift: _rShift = e.IsDown; break;
            case VkLControl: _lCtrl = e.IsDown; break;
            case VkRControl: _rCtrl = e.IsDown; break;
            case VkLWin: _lWin = e.IsDown; break;
            case VkRWin: _rWin = e.IsDown; break;
            default: isModifier = false; break;
        }

        Log? cancelLog = null;
        if (_pending != AltSide.None)
        {
            cancelLog = new Log($"Alt({_pending}): 取消（他キー vk=0x{e.Vk:X2} {(e.IsDown ? "down" : "up")}）");
            _pending = AltSide.None;
        }

        // 修飾キー自体のイベントは素通し
        var result = isModifier ? KeyResult.PassThrough : ProcessConversion(e, conversionEnabled);
        if (cancelLog == null)
        {
            return result;
        }

        return new KeyResult(result.Suppress, result.Actions.Prepend(cancelLog).ToArray());
    }

    private KeyResult ProcessConversion(KeyEvent e, bool conversionEnabled)
    {
        // VK_PACKET（Unicode 入力）の scanCode は文字コードなので対象外
        if (e.Vk == VkPacket || e.Extended || !ConvertibleScanCodes.Contains(e.ScanCode))
        {
            return KeyResult.PassThrough;
        }

        int scan = e.ScanCode;
        if (!e.IsDown)
        {
            // 押下を変換したキーは離しも握りつぶす（変換時に down/up を送り済み）
            return _convertibleDown.Remove(scan, out bool converted) && converted
                ? new KeyResult(true, Array.Empty<KeyAction>())
                : KeyResult.PassThrough;
        }

        bool shift = _lShift || _rShift;
        bool isRepeat = _convertibleDown.TryGetValue(scan, out bool wasConverted);
        if (!conversionEnabled || AnyCtrlAltWin || !UsToJis.TryGetValue((scan, shift), out var target))
        {
            // 元のキーをそのまま渡す（ショートカットや、US と JIS で同じ文字になる組み合わせ）
            _convertibleDown[scan] = false;
            return KeyResult.PassThrough;
        }

        _convertibleDown[scan] = true;
        if (scan == ScanCapsLock && isRepeat && wasConverted)
        {
            // CapsLock はリピートで切り替わり続けないよう最初の1回だけ
            return new KeyResult(true, Array.Empty<KeyAction>());
        }

        if (target.ScanCode != scan && _convertibleDown.TryGetValue(target.ScanCode, out bool targetConverted) && !targetConverted)
        {
            // 送信先のキーが素通しで押下中（例：- を押したまま =）。送信の up でシステム上は離れるので、
            // そのキーの後の物理的な離しは握りつぶす（対応する down の無い up を渡さない）
            _convertibleDown[target.ScanCode] = true;
        }

        var actions = new List<KeyAction>(8);
        if (isRepeat && !wasConverted)
        {
            // 素通しで押下中だった元のキーを離してから変換する（押しっぱなし防止）
            actions.Add(new SendKey(e.Vk, scan, false, true));
        }

        // Shift を一時的に調整し、送信後に物理状態（左右別）へ戻す
        bool releaseShift = !target.Shift && shift;
        bool pressShift = target.Shift && !shift;
        if (releaseShift)
        {
            if (_lShift) actions.Add(new SendKey(VkLShift, ScanLShift, false, true));
            if (_rShift) actions.Add(new SendKey(VkRShift, ScanRShift, false, true));
        }
        else if (pressShift)
        {
            actions.Add(new SendKey(VkLShift, ScanLShift, false, false));
        }

        actions.Add(new SendKey(0, target.ScanCode, false, false));
        actions.Add(new SendKey(0, target.ScanCode, false, true));

        if (releaseShift)
        {
            if (_lShift) actions.Add(new SendKey(VkLShift, ScanLShift, false, false));
            if (_rShift) actions.Add(new SendKey(VkRShift, ScanRShift, false, false));
        }
        else if (pressShift)
        {
            actions.Add(new SendKey(VkLShift, ScanLShift, false, true));
        }

        actions.Add(new Log($"変換 {target.Label}{(isRepeat ? "（リピート）" : "")}"));
        return new KeyResult(true, actions);
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
