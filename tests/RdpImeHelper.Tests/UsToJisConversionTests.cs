using RdpImeHelper.Logic;
using static RdpImeHelper.Logic.KeyProcessor;

namespace RdpImeHelper.Tests;

public class UsToJisConversionTests
{
    private readonly KeyProcessor _p = new();

    // ---- ヘルパ ----

    private static KeyEvent Key(int scan, bool down, uint time = 1000) => new(0x100 + scan, scan, false, down, time);

    private static KeyEvent Mod(int vk, bool down) => vk switch
    {
        VkLShift => new(vk, ScanLShift, false, down, 0),
        VkRShift => new(vk, ScanRShift, false, down, 0),
        VkLControl => new(vk, 0x1D, false, down, 0),
        VkRControl => new(vk, 0x1D, true, down, 0),
        VkLMenu => new(vk, 0x38, false, down, 0),
        VkRMenu => new(vk, 0x38, true, down, 0),
        VkLWin => new(vk, 0x5B, true, down, 0),
        VkRWin => new(vk, 0x5C, true, down, 0),
        _ => throw new ArgumentException(null, nameof(vk)),
    };

    private KeyResult Press(int scan, uint time = 1000) => _p.Process(Key(scan, true, time), true);

    private KeyResult Release(int scan) => _p.Process(Key(scan, false), true);

    private void Modifier(int vk, bool down)
    {
        var r = _p.Process(Mod(vk, down), true);
        Assert.False(r.Suppress);
        Assert.DoesNotContain(r.Actions, a => a is SendKey);
    }

    private static SendKey[] Sent(KeyResult r) => r.Actions.OfType<SendKey>().ToArray();

    private static SendKey LShift(bool up) => new(VkLShift, ScanLShift, false, up);

    private static SendKey RShift(bool up) => new(VkRShift, ScanRShift, false, up);

    private static SendKey[] Tap(int scan) => new[] { new SendKey(0, scan, false, false), new SendKey(0, scan, false, true) };

    /// <summary>物理 Shift（左右）の状態から、期待される送信列を組み立てる。</summary>
    private static SendKey[] Expected(JisKey target, bool l, bool r)
    {
        var list = new List<SendKey>();
        bool shift = l || r;
        if (target.Shift && !shift)
        {
            list.Add(LShift(false));
            list.AddRange(Tap(target.ScanCode));
            list.Add(LShift(true));
        }
        else if (!target.Shift && shift)
        {
            if (l) list.Add(LShift(true));
            if (r) list.Add(RShift(true));
            list.AddRange(Tap(target.ScanCode));
            if (l) list.Add(LShift(false));
            if (r) list.Add(RShift(false));
        }
        else
        {
            list.AddRange(Tap(target.ScanCode));
        }

        return list.ToArray();
    }

    /// <summary>一連の送信で Shift が最終的にどう変化したか（左右別）。null なら変化なし。</summary>
    private static (bool? L, bool? R) NetShift(IEnumerable<SendKey> keys)
    {
        bool? l = null, r = null;
        foreach (var k in keys)
        {
            if (k.ScanCode == ScanLShift) l = !k.KeyUp;
            if (k.ScanCode == ScanRShift) r = !k.KeyUp;
        }

        return (l, r);
    }

    private void HoldShift(bool l, bool r)
    {
        if (l) Modifier(VkLShift, true);
        if (r) Modifier(VkRShift, true);
    }

    // 全エントリ × 物理 Shift 状態（入力が Shift ありなら 左のみ/右のみ/左右両方）
    public static TheoryData<int, bool, bool> AllEntriesWithShiftSides()
    {
        var data = new TheoryData<int, bool, bool>();
        foreach (var (scan, shift) in UsToJis.Keys)
        {
            if (shift)
            {
                data.Add(scan, true, false);
                data.Add(scan, false, true);
                data.Add(scan, true, true);
            }
            else
            {
                data.Add(scan, false, false);
            }
        }

        return data;
    }

    public static TheoryData<int, bool> AllEntries()
    {
        var data = new TheoryData<int, bool>();
        foreach (var (scan, shift) in UsToJis.Keys)
        {
            data.Add(scan, shift);
        }

        return data;
    }

    public static TheoryData<int, bool, bool, int> AllEntriesWithCtrlAltWin()
    {
        var data = new TheoryData<int, bool, bool, int>();
        foreach (var row in AllEntriesWithShiftSides())
        {
            foreach (int vk in new[] { VkLControl, VkRControl, VkLMenu, VkRMenu, VkLWin, VkRWin })
            {
                data.Add((int)row[0], (bool)row[1], (bool)row[2], vk);
            }
        }

        return data;
    }

    public static TheoryData<int> ShiftedEntries()
    {
        var data = new TheoryData<int>();
        foreach (var (scan, shift) in UsToJis.Keys.Where(k => k.Shift))
        {
            data.Add(scan);
        }

        return data;
    }

    // ---- 変換表：全キー突き合わせ ----

    // US 101 配列（kbdus）：スキャンコード → (Shiftなし, Shiftあり)
    private static readonly Dictionary<int, (string Normal, string Shift)> UsLayout = new()
    {
        [0x29] = ("`", "~"),
        [0x02] = ("1", "!"), [0x03] = ("2", "@"), [0x04] = ("3", "#"), [0x05] = ("4", "$"),
        [0x06] = ("5", "%"), [0x07] = ("6", "^"), [0x08] = ("7", "&"), [0x09] = ("8", "*"),
        [0x0A] = ("9", "("), [0x0B] = ("0", ")"), [0x0C] = ("-", "_"), [0x0D] = ("=", "+"),
        [0x10] = ("q", "Q"), [0x11] = ("w", "W"), [0x12] = ("e", "E"), [0x13] = ("r", "R"),
        [0x14] = ("t", "T"), [0x15] = ("y", "Y"), [0x16] = ("u", "U"), [0x17] = ("i", "I"),
        [0x18] = ("o", "O"), [0x19] = ("p", "P"), [0x1A] = ("[", "{"), [0x1B] = ("]", "}"),
        [0x2B] = ("\\", "|"),
        [0x1E] = ("a", "A"), [0x1F] = ("s", "S"), [0x20] = ("d", "D"), [0x21] = ("f", "F"),
        [0x22] = ("g", "G"), [0x23] = ("h", "H"), [0x24] = ("j", "J"), [0x25] = ("k", "K"),
        [0x26] = ("l", "L"), [0x27] = (";", ":"), [0x28] = ("'", "\""),
        [0x2C] = ("z", "Z"), [0x2D] = ("x", "X"), [0x2E] = ("c", "C"), [0x2F] = ("v", "V"),
        [0x30] = ("b", "B"), [0x31] = ("n", "N"), [0x32] = ("m", "M"), [0x33] = (",", "<"),
        [0x34] = (".", ">"), [0x35] = ("/", "?"),
        [0x39] = (" ", " "),
    };

    // JIS 106/109 配列（kbd106）：スキャンコード → (Shiftなし, Shiftあり)。文字以外は <...> で表す
    private static readonly Dictionary<int, (string Normal, string Shift)> JisLayout = new()
    {
        [0x29] = ("<半角/全角>", "<半角/全角>"),
        [0x02] = ("1", "!"), [0x03] = ("2", "\""), [0x04] = ("3", "#"), [0x05] = ("4", "$"),
        [0x06] = ("5", "%"), [0x07] = ("6", "&"), [0x08] = ("7", "'"), [0x09] = ("8", "("),
        [0x0A] = ("9", ")"), [0x0B] = ("0", "<なし>"), [0x0C] = ("-", "="), [0x0D] = ("^", "~"),
        [0x7D] = ("<¥>", "|"),
        [0x10] = ("q", "Q"), [0x11] = ("w", "W"), [0x12] = ("e", "E"), [0x13] = ("r", "R"),
        [0x14] = ("t", "T"), [0x15] = ("y", "Y"), [0x16] = ("u", "U"), [0x17] = ("i", "I"),
        [0x18] = ("o", "O"), [0x19] = ("p", "P"), [0x1A] = ("@", "`"), [0x1B] = ("[", "{"),
        [0x1E] = ("a", "A"), [0x1F] = ("s", "S"), [0x20] = ("d", "D"), [0x21] = ("f", "F"),
        [0x22] = ("g", "G"), [0x23] = ("h", "H"), [0x24] = ("j", "J"), [0x25] = ("k", "K"),
        [0x26] = ("l", "L"), [0x27] = (";", "+"), [0x28] = (":", "*"), [0x2B] = ("]", "}"),
        [0x2C] = ("z", "Z"), [0x2D] = ("x", "X"), [0x2E] = ("c", "C"), [0x2F] = ("v", "V"),
        [0x30] = ("b", "B"), [0x31] = ("n", "N"), [0x32] = ("m", "M"), [0x33] = (",", "<"),
        [0x34] = (".", ">"), [0x35] = ("/", "?"), [0x73] = ("\\", "_"),
        [0x39] = (" ", " "),
    };

    private static string JisChar(int scan, bool shift) =>
        JisLayout.TryGetValue(scan, out var c) ? (shift ? c.Shift : c.Normal) : $"<0x{scan:X2}>";

    public static TheoryData<int, bool> AllUsPrintableKeys()
    {
        var data = new TheoryData<int, bool>();
        foreach (int scan in UsLayout.Keys)
        {
            data.Add(scan, false);
            data.Add(scan, true);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllUsPrintableKeys))]
    public void EveryUsKey_ProducesUsKeycapCharOnJis(int scan, bool shift)
    {
        string expected = shift ? UsLayout[scan].Shift : UsLayout[scan].Normal;

        HoldShift(shift, false);
        var r = Press(scan);

        string actual;
        if (!r.Suppress)
        {
            // 素通し：JIS がそのまま解釈する
            Assert.Empty(Sent(r));
            actual = JisChar(scan, shift);
        }
        else
        {
            // 変換：送信列の中で押された非 Shift キーと、そのときの Shift 状態から JIS の文字を求める
            bool l = shift;
            string? produced = null;
            foreach (var k in Sent(r))
            {
                if (k.ScanCode == ScanLShift) { l = !k.KeyUp; continue; }
                if (!k.KeyUp)
                {
                    Assert.Null(produced);
                    produced = JisChar(k.ScanCode, l);
                }
            }

            actual = produced ?? "<送信なし>";
        }

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Table_TargetsAreNonExtendedJisKeys()
    {
        foreach (var target in UsToJis.Values.Where(t => t.ScanCode != ScanCapsLock))
        {
            Assert.True(JisLayout.ContainsKey(target.ScanCode), $"0x{target.ScanCode:X2}");
        }
    }

    [Fact]
    public void Table_HasNoIdentityEntries()
    {
        // 送信内容が入力と同じになるエントリは不要（CapsLock は Shift を足すので対象外）
        foreach (var ((scan, shift), target) in UsToJis)
        {
            Assert.False(scan == target.ScanCode && shift == target.Shift, $"0x{scan:X2} shift={shift}");
        }
    }

    [Fact]
    public void BackQuote_IsAlwaysIntercepted()
    {
        // US の ` は JIS の半角/全角と同位置：Shift の有無にかかわらず素通ししない
        Assert.True(Press(0x29).Suppress);
        Assert.True(Release(0x29).Suppress);
        HoldShift(true, false);
        Assert.True(Press(0x29).Suppress);
        Assert.True(Release(0x29).Suppress);
    }

    // ---- 全エントリ：Shift の有無・左右 ----

    [Theory]
    [MemberData(nameof(AllEntriesWithShiftSides))]
    public void Entry_ConvertsWithCorrectShiftHandling(int scan, bool l, bool r)
    {
        var target = UsToJis[(scan, l || r)];
        HoldShift(l, r);

        var down = Press(scan);
        Assert.True(down.Suppress);
        Assert.Equal(Expected(target, l, r), Sent(down));

        var up = Release(scan);
        Assert.True(up.Suppress);
        Assert.Empty(up.Actions);
    }

    [Theory]
    [MemberData(nameof(AllEntriesWithShiftSides))]
    public void Entry_RestoresPhysicalShiftExactly(int scan, bool l, bool r)
    {
        HoldShift(l, r);
        var sent = Sent(Press(scan));

        // 送信後の Shift 状態は物理状態と一致（左右とも）
        var (netL, netR) = NetShift(sent);
        Assert.True(netL is null || netL == l, $"L: net={netL} physical={l}");
        Assert.True(netR is null || netR == r, $"R: net={netR} physical={r}");

        // 押していない側の Shift を離したり、押している側を押し直したりしない
        if (!r) Assert.DoesNotContain(sent, k => k.ScanCode == ScanRShift);
    }

    [Theory]
    [MemberData(nameof(AllEntries))]
    public void Entry_LogsConversion(int scan, bool shift)
    {
        HoldShift(shift, false);
        var r = Press(scan);
        Assert.Contains(r.Actions, a => a is Log log && log.Message.Contains(UsToJis[(scan, shift)].Label));
    }

    // ---- キーリピート ----

    [Theory]
    [MemberData(nameof(AllEntriesWithShiftSides))]
    public void Entry_KeyRepeat_ConvertsEachRepeat(int scan, bool l, bool r)
    {
        var target = UsToJis[(scan, l || r)];
        HoldShift(l, r);

        var first = Press(scan, 1000);
        Assert.True(first.Suppress);
        for (uint t = 1500; t < 1700; t += 33)
        {
            var repeat = Press(scan, t);
            Assert.True(repeat.Suppress);
            if (scan == ScanCapsLock)
            {
                // CapsLock はリピートで切り替わり続けないよう送らない
                Assert.Empty(Sent(repeat));
            }
            else
            {
                Assert.Equal(Expected(target, l, r), Sent(repeat));
            }
        }

        var up = Release(scan);
        Assert.True(up.Suppress);
        Assert.Empty(up.Actions);
    }

    // ---- Ctrl/Alt/Win 併用時は変換しない ----

    [Theory]
    [MemberData(nameof(AllEntriesWithCtrlAltWin))]
    public void Entry_WithCtrlAltWin_IsNotConverted(int scan, bool l, bool r, int modifier)
    {
        HoldShift(l, r);
        Modifier(modifier, true);

        for (int i = 0; i < 3; i++)
        {
            var down = Press(scan);
            Assert.False(down.Suppress);
            Assert.Empty(Sent(down));
        }

        var up = Release(scan);
        Assert.False(up.Suppress);
        Assert.Empty(Sent(up));

        Modifier(modifier, false);
    }

    [Theory]
    [MemberData(nameof(AllEntriesWithShiftSides))]
    public void Entry_AfterCtrlReleased_IsConvertedAgain(int scan, bool l, bool r)
    {
        HoldShift(l, r);
        Modifier(VkRControl, true);
        Assert.False(Press(scan).Suppress);
        Assert.False(Release(scan).Suppress);
        Modifier(VkRControl, false);

        // 前回の素通しの名残（元キーの up やリピート扱い）が無いこと
        var again = Press(scan);
        Assert.True(again.Suppress);
        Assert.Equal(Expected(UsToJis[(scan, l || r)], l, r), Sent(again));
        Assert.DoesNotContain(again.Actions, a => a is Log log && log.Message.Contains("リピート"));
        Assert.True(Release(scan).Suppress);
    }

    [Theory]
    [MemberData(nameof(AllEntriesWithShiftSides))]
    public void Entry_CtrlPressedDuringRepeat_ReleasesNothingStuck(int scan, bool l, bool r)
    {
        // 変換中に Ctrl を押す → リピートは素通し → 離しも素通し（元キーの down が渡っているので）
        HoldShift(l, r);
        Assert.True(Press(scan).Suppress);
        Modifier(VkLControl, true);
        Assert.Equal(KeyResult.PassThrough, Press(scan, 1500));
        Assert.Equal(KeyResult.PassThrough, Release(scan));
    }

    [Theory]
    [MemberData(nameof(AllEntriesWithShiftSides))]
    public void Entry_CtrlReleasedDuringRepeat_ReleasesOriginalBeforeConverting(int scan, bool l, bool r)
    {
        // Ctrl+キーで素通し中に Ctrl を離す → 次のリピートは変換。素通しで押下中の元キーを先に離す
        HoldShift(l, r);
        Modifier(VkLControl, true);
        Assert.False(Press(scan).Suppress);
        Modifier(VkLControl, false);

        var repeat = Press(scan, 1500);
        Assert.True(repeat.Suppress);
        var expected = new[] { new SendKey(0x100 + scan, scan, false, true) }
            .Concat(Expected(UsToJis[(scan, l || r)], l, r));
        Assert.Equal(expected, Sent(repeat));

        Assert.True(Release(scan).Suppress);
    }

    [Theory]
    [MemberData(nameof(AllEntriesWithShiftSides))]
    public void Entry_PressReleasePress_ConvertsBothTimesIdentically(int scan, bool l, bool r)
    {
        HoldShift(l, r);
        var expected = Expected(UsToJis[(scan, l || r)], l, r);
        for (int i = 0; i < 2; i++)
        {
            var down = Press(scan, (uint)(1000 + i * 100));
            Assert.True(down.Suppress);
            Assert.Equal(expected, Sent(down));
            Assert.DoesNotContain(down.Actions, a => a is Log log && log.Message.Contains("リピート"));
            Assert.True(Release(scan).Suppress);
        }
    }

    [Theory]
    [MemberData(nameof(AllEntriesWithShiftSides))]
    public void Entry_ConvertedKeyUpDuringAlt_IsStillSuppressed(int scan, bool l, bool r)
    {
        // 変換済みキーを押したまま Alt を押し、キーを離す：離しは握りつぶし、Alt 単押しは取り消す
        HoldShift(l, r);
        Assert.True(Press(scan).Suppress);
        _p.Process(Mod(VkLMenu, true) with { Time = 1000 }, true);

        var up = Release(scan);
        Assert.True(up.Suppress);
        Assert.Empty(Sent(up));

        var altUp = _p.Process(Mod(VkLMenu, false) with { Time = 1050 }, true);
        Assert.DoesNotContain(altUp.Actions, a => a is SetIme);
    }

    [Theory]
    [MemberData(nameof(AllEntries))]
    public void Entry_PassedKeyUpDuringAlt_PassesThrough(int scan, bool shift)
    {
        // Ctrl 併用で素通しした down の離しは、Alt 押下中でも素通し
        HoldShift(shift, false);
        Modifier(VkLControl, true);
        Assert.False(Press(scan).Suppress);
        Modifier(VkLControl, false);
        _p.Process(Mod(VkLMenu, true), true);
        Assert.False(Release(scan).Suppress);
    }

    // ---- 変換の無効条件 ----

    [Theory]
    [MemberData(nameof(AllEntries))]
    public void Entry_WhenConversionDisabled_PassesThrough(int scan, bool shift)
    {
        if (shift) Assert.False(_p.Process(Mod(VkLShift, true), false).Suppress);
        var down = _p.Process(Key(scan, true), false);
        Assert.Equal(KeyResult.PassThrough, down);
        Assert.Equal(KeyResult.PassThrough, _p.Process(Key(scan, false), false));
    }

    [Fact]
    public void DisabledBetweenDownAndUp_UpIsStillSuppressed()
    {
        Assert.True(Press(0x1A).Suppress);
        Assert.True(_p.Process(Key(0x1A, false), false).Suppress);
    }

    [Fact]
    public void EnabledBetweenDownAndUp_UpPassesThrough()
    {
        Assert.False(_p.Process(Key(0x1A, true), false).Suppress);
        Assert.False(Release(0x1A).Suppress);
    }

    [Theory]
    [MemberData(nameof(AllEntries))]
    public void ExtendedScanCode_IsNotConverted(int scan, bool shift)
    {
        // 変換表と同じ下位バイトでも E0 付きは別キー
        HoldShift(shift, false);
        Assert.Equal(KeyResult.PassThrough, _p.Process(new KeyEvent(0x100 + scan, scan, true, true, 0), true));
        Assert.Equal(KeyResult.PassThrough, _p.Process(new KeyEvent(0x100 + scan, scan, true, false, 0), true));
    }

    [Theory]
    [MemberData(nameof(AllEntries))]
    public void VkPacket_IsNotConverted(int scan, bool shift)
    {
        // VK_PACKET（Unicode 入力）の scanCode は文字コード。')' = 0x29 などが変換表と衝突しても素通し
        HoldShift(shift, false);
        Assert.Equal(KeyResult.PassThrough, _p.Process(new KeyEvent(VkPacket, scan, false, true, 0), true));
        Assert.Equal(KeyResult.PassThrough, _p.Process(new KeyEvent(VkPacket, scan, false, false, 0), true));
    }

    [Fact]
    public void UnchangedKeys_PassThrough()
    {
        // US と JIS で同じ文字になるキー（例：2, -, ;）は素通し
        foreach (int scan in new[] { 0x03, 0x0C, 0x27, 0x07, 0x0B })
        {
            Assert.Equal(KeyResult.PassThrough, Press(scan));
            Assert.Equal(KeyResult.PassThrough, Release(scan));
        }
    }

    [Fact]
    public void ModifierEvents_AlwaysPassThrough()
    {
        foreach (int vk in new[] { VkLShift, VkRShift, VkLControl, VkRControl, VkLWin, VkRWin })
        {
            Assert.Equal(KeyResult.PassThrough, _p.Process(Mod(vk, true), true));
            Assert.Equal(KeyResult.PassThrough, _p.Process(Mod(vk, false), true));
        }
    }

    // ---- 変換途中で Shift が離された・押された場合の復元 ----

    [Theory]
    [MemberData(nameof(ShiftedEntries))]
    public void ShiftReleasedDuringRepeat_FollowsPhysicalState(int scan)
    {
        foreach (var (l, r) in new[] { (true, false), (false, true) })
        {
            var p = new KeyProcessor();
            p.Process(Mod(l ? VkLShift : VkRShift, true), true);
            Assert.True(p.Process(Key(scan, true), true).Suppress);

            // Shift を離す（素通し）
            Assert.Equal(KeyResult.PassThrough, p.Process(Mod(l ? VkLShift : VkRShift, false), true));

            // 以降のリピートは Shift なしとして扱い、Shift を押し直さない
            var repeat = p.Process(Key(scan, true, 1500), true);
            var sent = Sent(repeat);
            Assert.DoesNotContain(sent, k => k.ScanCode == ScanRShift);
            var net = NetShift(sent);
            Assert.True(net.L is null or false && net.R is null or false, "Shift が押しっぱなしになる");

            if (UsToJis.TryGetValue((scan, false), out var unshifted))
            {
                Assert.True(repeat.Suppress);
                Assert.Equal(Expected(unshifted, false, false), sent);
                Assert.True(p.Process(Key(scan, false), true).Suppress);
            }
            else
            {
                // Shift なしは US/JIS 同じ文字：元キーを素通し
                Assert.False(repeat.Suppress);
                Assert.Empty(sent);
                Assert.False(p.Process(Key(scan, false), true).Suppress);
            }
        }
    }

    [Theory]
    [MemberData(nameof(ShiftedEntries))]
    public void OneOfTwoShiftsReleasedDuringRepeat_RestoresOnlyRemainingSide(int scan)
    {
        var target = UsToJis[(scan, true)];
        foreach (bool releaseLeft in new[] { true, false })
        {
            var p = new KeyProcessor();
            p.Process(Mod(VkLShift, true), true);
            p.Process(Mod(VkRShift, true), true);
            Assert.True(p.Process(Key(scan, true), true).Suppress);

            p.Process(Mod(releaseLeft ? VkLShift : VkRShift, false), true);
            var sent = Sent(p.Process(Key(scan, true, 1500), true));

            if (scan == ScanCapsLock)
            {
                continue;
            }

            Assert.Equal(Expected(target, !releaseLeft, releaseLeft), sent);
            // 離した側の Shift には一切触れない
            Assert.DoesNotContain(sent, k => k.ScanCode == (releaseLeft ? ScanLShift : ScanRShift));
        }
    }

    [Theory]
    [MemberData(nameof(AllEntries))]
    public void ShiftPressedDuringRepeat_FollowsPhysicalState(int scan, bool shift)
    {
        if (shift)
        {
            return;
        }

        Assert.True(Press(scan).Suppress);
        Modifier(VkRShift, true);
        var repeat = Press(scan, 1500);
        var sent = Sent(repeat);

        // 押したままの右 Shift を離したら必ず押し直す
        var net = NetShift(sent);
        Assert.True(net.R is null or true, "右 Shift が離れたままになる");
        Assert.True(net.L is null or false, "左 Shift が押しっぱなしになる");

        if (UsToJis.TryGetValue((scan, true), out var shifted))
        {
            Assert.True(repeat.Suppress);
            if (scan != ScanCapsLock)
            {
                Assert.Equal(Expected(shifted, false, true), sent);
            }
        }
        else
        {
            // Shift ありは US/JIS で同じ文字：リピートは元キーを素通しし、離しも素通しする
            Assert.False(repeat.Suppress);
            Assert.False(Release(scan).Suppress);
        }
    }

    [Fact]
    public void ShiftStateDoesNotLeak_AcrossManyConversions()
    {
        // 左 Shift を押したまま Shift なし変換（@）と Shift あり維持（&）を交互に行っても、
        // 各送信の差し引きで Shift 状態は物理状態のまま
        Modifier(VkLShift, true);
        for (int i = 0; i < 10; i++)
        {
            var sent = Sent(Press(i % 2 == 0 ? 0x03 : 0x08, (uint)(1000 + i)));
            Assert.True(NetShift(sent).L is null or true);
            Release(i % 2 == 0 ? 0x03 : 0x08);
        }

        Modifier(VkLShift, false);
        var after = Sent(Press(0x0D));
        Assert.Equal(new[] { LShift(false), Tap(0x0C)[0], Tap(0x0C)[1], LShift(true) }, after);
    }

    // ---- 送信先キーが素通しで押下中（ロールオーバー） ----

    [Fact]
    public void Rollover_TargetKeyHeldAsPassThrough_ItsPhysicalUpIsSuppressed()
    {
        // - を押したまま = を押す：= は Shift+0x0C を送る（0x0C の up を含む）
        Assert.Equal(KeyResult.PassThrough, Press(0x0C));
        var eq = Press(0x0D);
        Assert.True(eq.Suppress);
        Assert.Equal(new[] { LShift(false), Tap(0x0C)[0], Tap(0x0C)[1], LShift(true) }, Sent(eq));

        // システム上 0x0C は既に離れているので、物理的な - の離しは渡さない
        var minusUp = Release(0x0C);
        Assert.True(minusUp.Suppress);
        Assert.Empty(minusUp.Actions);
        Assert.True(Release(0x0D).Suppress);

        // 以降は通常どおり
        Assert.Equal(KeyResult.PassThrough, Press(0x0C));
        Assert.Equal(KeyResult.PassThrough, Release(0x0C));
    }

    [Fact]
    public void Rollover_TargetKeyHeld_RepeatAfterInjectedUpPassesAgain()
    {
        // 7 を押したまま ' を押す（Shift+0x08）→ 7 のリピートは素通しの down、離しも素通し
        Assert.Equal(KeyResult.PassThrough, Press(0x08));
        Assert.True(Press(0x28).Suppress);
        Assert.True(Release(0x28).Suppress);
        Assert.Equal(KeyResult.PassThrough, Press(0x08, 1500));
        Assert.Equal(KeyResult.PassThrough, Release(0x08));
    }

    // ---- 状態リセット（ロック・セキュアデスクトップ・切断で key-up を取りこぼした場合） ----

    [Fact]
    public void Reset_AfterMissedShiftUp_DoesNotRepressShift()
    {
        Modifier(VkLShift, true);
        // （Shift の up はロック画面で取りこぼし）
        Assert.Contains(_p.Reset("test").Actions, a => a is Log);

        // 2 は Shift なし扱い：素通しし、Shift を押し直さない
        Assert.Equal(KeyResult.PassThrough, Press(0x03));
        Assert.Equal(KeyResult.PassThrough, Release(0x03));
        var eq = Sent(Press(0x0D));
        Assert.True(NetShift(eq).L is null or false);
    }

    [Theory]
    [InlineData(VkLWin)]
    [InlineData(VkRWin)]
    [InlineData(VkLControl)]
    [InlineData(VkLMenu)]
    public void Reset_AfterMissedModifierUp_ConversionAndAltTapWorkAgain(int modifier)
    {
        _p.Process(Mod(modifier, true), true);
        Assert.False(Press(0x1A).Suppress);
        Release(0x1A);

        _p.Reset("test");
        Assert.True(Press(0x1A).Suppress);
        Assert.True(Release(0x1A).Suppress);

        _p.Process(Mod(VkLMenu, true) with { Time = 2000 }, true);
        var r = _p.Process(Mod(VkLMenu, false) with { Time = 2050 }, true);
        Assert.Contains(new SetIme(false), r.Actions);
    }

    [Fact]
    public void Reset_AfterMissedKeyUp_NextPressIsFresh()
    {
        Assert.True(Press(ScanCapsLock).Suppress);
        // （CapsLock の up を取りこぼし）
        _p.Reset("test");
        var again = Press(ScanCapsLock);
        Assert.Equal(new[] { LShift(false), Tap(ScanCapsLock)[0], Tap(ScanCapsLock)[1], LShift(true) }, Sent(again));
    }

    // ---- CapsLock ----

    [Fact]
    public void CapsLock_EachPressToggles()
    {
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(new[] { LShift(false), Tap(ScanCapsLock)[0], Tap(ScanCapsLock)[1], LShift(true) }, Sent(Press(ScanCapsLock)));
            Assert.True(Release(ScanCapsLock).Suppress);
        }
    }

    [Fact]
    public void CapsLock_SendsShiftPlusCapsLock()
    {
        var r = Press(ScanCapsLock);
        Assert.True(r.Suppress);
        Assert.Equal(new[] { LShift(false), Tap(ScanCapsLock)[0], Tap(ScanCapsLock)[1], LShift(true) }, Sent(r));
        Assert.True(Release(ScanCapsLock).Suppress);
    }

    [Fact]
    public void ShiftCapsLock_PassesThrough()
    {
        Modifier(VkLShift, true);
        Assert.Equal(KeyResult.PassThrough, Press(ScanCapsLock));
        Assert.Equal(KeyResult.PassThrough, Release(ScanCapsLock));
    }

    // ---- Alt 単押しとの共存 ----

    [Fact]
    public void ConvertedKeyDuringAlt_CancelsAltTap_AndIsNotConverted()
    {
        _p.Process(Mod(VkLMenu, true), true);
        var r = Press(0x1A);
        Assert.False(r.Suppress);
        Assert.Contains(r.Actions, a => a is Log l && l.Message.Contains("取消"));
        Assert.False(Release(0x1A).Suppress);
        var altUp = _p.Process(Mod(VkLMenu, false), true);
        Assert.DoesNotContain(altUp.Actions, a => a is SetIme);
    }

    [Fact]
    public void AltTap_StillWorksWithConversionEnabled()
    {
        _p.Process(Mod(VkRMenu, true) with { Time = 1000 }, true);
        var r = _p.Process(Mod(VkRMenu, false) with { Time = 1100 }, true);
        Assert.True(r.Suppress);
        Assert.Contains(new SetIme(true), r.Actions);
    }
}
