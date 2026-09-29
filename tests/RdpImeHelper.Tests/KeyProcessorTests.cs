using RdpImeHelper.Logic;
using static RdpImeHelper.Logic.KeyProcessor;

namespace RdpImeHelper.Tests;

public class KeyProcessorAltTapTests
{
    private const int VkA = 0x41;
    private const int VkTab = 0x09;

    private readonly KeyProcessor _p = new();

    private static KeyEvent Down(int vk, uint time) => Ev(vk, true, time);
    private static KeyEvent Up(int vk, uint time) => Ev(vk, false, time);

    private static KeyEvent Ev(int vk, bool down, uint time) => vk switch
    {
        VkLMenu => new(vk, 0x38, false, down, time),
        VkRMenu => new(vk, 0x38, true, down, time),
        VkLShift => new(vk, 0x2A, false, down, time),
        VkRShift => new(vk, 0x36, false, down, time),
        VkLControl => new(vk, 0x1D, false, down, time),
        VkRControl => new(vk, 0x1D, true, down, time),
        VkLWin => new(vk, 0x5B, true, down, time),
        VkRWin => new(vk, 0x5C, true, down, time),
        VkTab => new(vk, 0x0F, false, down, time),
        _ => new(vk, 0x1E, false, down, time),
    };

    private void AssertPassThroughAll(params KeyEvent[] events)
    {
        foreach (var e in events)
        {
            var r = _p.Process(e);
            Assert.False(r.Suppress);
            Assert.DoesNotContain(r.Actions, a => a is SetIme or SendKey);
        }
    }

    private static void AssertTap(KeyResult r, bool right)
    {
        Assert.True(r.Suppress);
        var acts = r.Actions.Where(a => a is not Log).ToArray();
        Assert.Equal(
            new KeyAction[]
            {
                new SendKey(VkDummy, 0, false, false),
                new SendKey(VkDummy, 0, false, true),
                new SendKey(right ? VkRMenu : VkLMenu, ScanAlt, right, true),
                new SetIme(right),
            },
            acts);
    }

    private static void AssertNotTap(KeyResult r)
    {
        Assert.False(r.Suppress);
        Assert.DoesNotContain(r.Actions, a => a is SetIme or SendKey);
    }

    // ---- 単押し ----

    [Fact]
    public void LeftAltTap_TurnsImeOff()
    {
        AssertPassThroughAll(Down(VkLMenu, 1000));
        AssertTap(_p.Process(Up(VkLMenu, 1100)), right: false);
    }

    [Fact]
    public void RightAltTap_TurnsImeOn()
    {
        AssertPassThroughAll(Down(VkRMenu, 1000));
        AssertTap(_p.Process(Up(VkRMenu, 1100)), right: true);
    }

    [Fact]
    public void AltDown_IsNeverSuppressed()
    {
        Assert.Equal(KeyResult.PassThrough, _p.Process(Down(VkLMenu, 1000)));
    }

    [Fact]
    public void Tap_LogsDecision()
    {
        _p.Process(Down(VkRMenu, 1000));
        var r = _p.Process(Up(VkRMenu, 1050));
        Assert.Contains(r.Actions, a => a is Log l && l.Message.Contains("単押し"));
    }

    [Fact]
    public void ConsecutiveTaps_EachWork()
    {
        _p.Process(Down(VkLMenu, 1000));
        AssertTap(_p.Process(Up(VkLMenu, 1050)), right: false);
        _p.Process(Down(VkRMenu, 1100));
        AssertTap(_p.Process(Up(VkRMenu, 1150)), right: true);
        _p.Process(Down(VkLMenu, 1200));
        AssertTap(_p.Process(Up(VkLMenu, 1250)), right: false);
    }

    // ---- 長押し ----

    [Fact]
    public void AutoRepeatWithin500ms_IsStillTap()
    {
        AssertPassThroughAll(Down(VkLMenu, 1000), Down(VkLMenu, 1300), Down(VkLMenu, 1330));
        AssertTap(_p.Process(Up(VkLMenu, 1400)), right: false);
    }

    [Fact]
    public void Exactly500ms_IsTap()
    {
        _p.Process(Down(VkLMenu, 1000));
        AssertTap(_p.Process(Up(VkLMenu, 1500)), right: false);
    }

    [Fact]
    public void Over500ms_IsNotTap()
    {
        _p.Process(Down(VkLMenu, 1000));
        var r = _p.Process(Up(VkLMenu, 1501));
        AssertNotTap(r);
        Assert.Contains(r.Actions, a => a is Log l && l.Message.Contains("長押し"));
    }

    [Fact]
    public void LongPressWithAutoRepeat_IsNotTap()
    {
        AssertPassThroughAll(Down(VkRMenu, 1000), Down(VkRMenu, 1500), Down(VkRMenu, 1533), Down(VkRMenu, 1566));
        AssertNotTap(_p.Process(Up(VkRMenu, 2000)));
    }

    [Fact]
    public void TickCountWraparound_IsHandled()
    {
        _p.Process(Down(VkLMenu, uint.MaxValue - 50));
        AssertTap(_p.Process(Up(VkLMenu, 49)), right: false);
    }

    [Fact]
    public void TickCountWraparound_LongPress()
    {
        _p.Process(Down(VkLMenu, uint.MaxValue - 50));
        AssertNotTap(_p.Process(Up(VkLMenu, 600)));
    }

    // ---- 他キーとの組み合わせ ----

    [Fact]
    public void AltTab_IsNotTap()
    {
        AssertPassThroughAll(Down(VkLMenu, 1000), Down(VkTab, 1050), Up(VkTab, 1080));
        AssertNotTap(_p.Process(Up(VkLMenu, 1100)));
    }

    [Fact]
    public void AltWithOtherKey_RightSide_IsNotTap()
    {
        AssertPassThroughAll(Down(VkRMenu, 1000), Down(VkA, 1050), Up(VkA, 1080));
        AssertNotTap(_p.Process(Up(VkRMenu, 1100)));
    }

    [Fact]
    public void OtherKeyReleasedDuringAlt_IsNotTap()
    {
        // a を押したまま Alt を押し、a を離してから Alt を離す（ロールオーバー）
        AssertPassThroughAll(Down(VkA, 990), Down(VkLMenu, 1000), Up(VkA, 1020));
        AssertNotTap(_p.Process(Up(VkLMenu, 1100)));
    }

    [Fact]
    public void AltComboCancel_LogsReason()
    {
        _p.Process(Down(VkLMenu, 1000));
        var r = _p.Process(Down(VkTab, 1050));
        Assert.False(r.Suppress);
        Assert.Contains(r.Actions, a => a is Log l && l.Message.Contains("取消"));
    }

    [Fact]
    public void AfterCombo_NextTapWorks()
    {
        _p.Process(Down(VkLMenu, 1000));
        _p.Process(Down(VkTab, 1050));
        _p.Process(Up(VkTab, 1080));
        _p.Process(Up(VkLMenu, 1100));

        _p.Process(Down(VkLMenu, 2000));
        AssertTap(_p.Process(Up(VkLMenu, 2050)), right: false);
    }

    // ---- 左右同時押し ----

    [Fact]
    public void LeftThenRight_ReleaseRightFirst_NeitherIsTap()
    {
        AssertPassThroughAll(Down(VkLMenu, 1000), Down(VkRMenu, 1020));
        AssertNotTap(_p.Process(Up(VkRMenu, 1050)));
        AssertNotTap(_p.Process(Up(VkLMenu, 1080)));
    }

    [Fact]
    public void LeftThenRight_ReleaseLeftFirst_NeitherIsTap()
    {
        AssertPassThroughAll(Down(VkLMenu, 1000), Down(VkRMenu, 1020));
        AssertNotTap(_p.Process(Up(VkLMenu, 1050)));
        AssertNotTap(_p.Process(Up(VkRMenu, 1080)));
    }

    [Fact]
    public void RightThenLeft_NeitherIsTap()
    {
        AssertPassThroughAll(Down(VkRMenu, 1000), Down(VkLMenu, 1020));
        AssertNotTap(_p.Process(Up(VkLMenu, 1050)));
        AssertNotTap(_p.Process(Up(VkRMenu, 1080)));
    }

    [Fact]
    public void Overlapping_LeftReleasedThenRightReleased_RightIsNotTap()
    {
        // 左を押す → 右を押す → 左を離す → 右を離す：右は左が押されている間に押されたので対象外
        AssertPassThroughAll(Down(VkLMenu, 1000), Down(VkRMenu, 1020), Up(VkLMenu, 1040));
        AssertNotTap(_p.Process(Up(VkRMenu, 1060)));
    }

    [Fact]
    public void AfterSimultaneous_NextTapWorks()
    {
        _p.Process(Down(VkLMenu, 1000));
        _p.Process(Down(VkRMenu, 1020));
        _p.Process(Up(VkRMenu, 1050));
        _p.Process(Up(VkLMenu, 1080));

        _p.Process(Down(VkRMenu, 2000));
        AssertTap(_p.Process(Up(VkRMenu, 2050)), right: true);
    }

    // ---- 修飾キー併用 ----

    [Theory]
    [InlineData(VkLShift)]
    [InlineData(VkRShift)]
    [InlineData(VkLControl)]
    [InlineData(VkRControl)]
    [InlineData(VkLWin)]
    [InlineData(VkRWin)]
    public void ModifierHeldBeforeAlt_IsNotTap(int modifier)
    {
        AssertPassThroughAll(Down(modifier, 900));
        AssertPassThroughAll(Down(VkLMenu, 1000));
        AssertNotTap(_p.Process(Up(VkLMenu, 1050)));
        AssertPassThroughAll(Up(modifier, 1100));
    }

    [Theory]
    [InlineData(VkLShift)]
    [InlineData(VkRControl)]
    [InlineData(VkLWin)]
    public void ModifierHeldBeforeRightAlt_IsNotTap(int modifier)
    {
        _p.Process(Down(modifier, 900));
        _p.Process(Down(VkRMenu, 1000));
        AssertNotTap(_p.Process(Up(VkRMenu, 1050)));
    }

    [Theory]
    [InlineData(VkLShift)]
    [InlineData(VkRShift)]
    [InlineData(VkLControl)]
    [InlineData(VkLWin)]
    public void ModifierPressedDuringAlt_IsNotTap(int modifier)
    {
        _p.Process(Down(VkLMenu, 1000));
        _p.Process(Down(modifier, 1020));
        _p.Process(Up(modifier, 1040));
        AssertNotTap(_p.Process(Up(VkLMenu, 1060)));
    }

    [Fact]
    public void ModifierReleasedBeforeAlt_TapWorks()
    {
        _p.Process(Down(VkLShift, 900));
        _p.Process(Up(VkLShift, 950));
        _p.Process(Down(VkLMenu, 1000));
        AssertTap(_p.Process(Up(VkLMenu, 1050)), right: false);
    }

    [Fact]
    public void ModifierHeldBeforeAlt_LogsReason()
    {
        _p.Process(Down(VkLControl, 900));
        var r = _p.Process(Down(VkLMenu, 1000));
        Assert.False(r.Suppress);
        Assert.Contains(r.Actions, a => a is Log l && l.Message.Contains("併用"));
    }

    [Fact]
    public void OneOfTwoShiftsStillHeld_IsNotTap()
    {
        _p.Process(Down(VkLShift, 800));
        _p.Process(Down(VkRShift, 850));
        _p.Process(Up(VkLShift, 900));
        _p.Process(Down(VkLMenu, 1000));
        AssertNotTap(_p.Process(Up(VkLMenu, 1050)));
    }

    // ---- マウスクリック ----

    [Fact]
    public void MouseClickDuringAlt_IsNotTap()
    {
        _p.Process(Down(VkLMenu, 1000));
        var m = _p.OnMouseButtonDown();
        Assert.Contains(m.Actions, a => a is Log l && l.Message.Contains("マウス"));
        AssertNotTap(_p.Process(Up(VkLMenu, 1100)));
    }

    [Fact]
    public void MouseClickDuringRightAlt_IsNotTap()
    {
        _p.Process(Down(VkRMenu, 1000));
        _p.OnMouseButtonDown();
        AssertNotTap(_p.Process(Up(VkRMenu, 1100)));
    }

    [Fact]
    public void MouseClickBeforeAlt_TapWorks()
    {
        Assert.Empty(_p.OnMouseButtonDown().Actions);
        _p.Process(Down(VkLMenu, 1000));
        AssertTap(_p.Process(Up(VkLMenu, 1100)), right: false);
    }

    [Fact]
    public void MouseClickAfterAltUp_DoesNotAffectNextTap()
    {
        _p.Process(Down(VkLMenu, 1000));
        _p.OnMouseButtonDown();
        _p.Process(Up(VkLMenu, 1100));
        _p.OnMouseButtonDown();

        _p.Process(Down(VkRMenu, 2000));
        AssertTap(_p.Process(Up(VkRMenu, 2050)), right: true);
    }

    // ---- その他 ----

    [Fact]
    public void AltUpWithoutDown_PassesThrough()
    {
        // フック開始前から押されていた Alt の up
        AssertNotTap(_p.Process(Up(VkLMenu, 1000)));
    }

    [Fact]
    public void NormalKeys_PassThrough()
    {
        Assert.Equal(KeyResult.PassThrough, _p.Process(Down(VkA, 1000)));
        Assert.Equal(KeyResult.PassThrough, _p.Process(Up(VkA, 1010)));
    }
}
