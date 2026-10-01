using RdpImeHelper.Logic;

namespace RdpImeHelper.Tests;

public class InjectionOrderTests
{
    private readonly InjectionOrder _o = new();

    [Fact]
    public void NothingSent_PassesThrough()
    {
        Assert.False(_o.ShouldReinject(1000));
    }

    // 実機（2026-10-01）：左Shift+2 の変換「Shift↑ @ Shift↓」が、先に並んでいた物理 Shift↑ に追い越され、
    // 左Shift が押しっぱなしになった。送ったキーが戻るまでの物理キーは送り直して後ろに並べる
    [Fact]
    public void ConversionBatchPending_PhysicalShiftUpIsReinjected()
    {
        _o.OnSent(4, 1000);                    // Shift↑ 0x1A↓ 0x1A↑ Shift↓
        Assert.True(_o.ShouldReinject(1001));  // 先に並んでいた物理 Shift↑
        _o.OnSent(1, 1001);                    // 送り直した Shift↑（バッチの後ろ）

        for (int i = 0; i < 5; i++)
        {
            _o.OnOwnInjectedSeen();
        }

        Assert.Equal(0, _o.Pending);
        Assert.False(_o.ShouldReinject(1010)); // 以降は通常どおり素通し
    }

    [Fact]
    public void KeepsReinjectingUntilAllOwnKeysAreSeen()
    {
        _o.OnSent(2, 1000);
        _o.OnOwnInjectedSeen();
        Assert.True(_o.ShouldReinject(1002));
        _o.OnOwnInjectedSeen();
        Assert.False(_o.ShouldReinject(1003));
    }

    [Fact]
    public void ReinjectedKeysAreAlsoWaitedFor()
    {
        // 送り直したキーより後に届いた物理キーも、その後ろに並べる
        _o.OnSent(1, 1000);
        Assert.True(_o.ShouldReinject(1001));
        _o.OnSent(1, 1001);
        _o.OnOwnInjectedSeen();
        Assert.True(_o.ShouldReinject(1002));
        _o.OnOwnInjectedSeen();
        Assert.False(_o.ShouldReinject(1003));
    }

    [Fact]
    public void LostOwnKeys_TimeOut()
    {
        // 送ったキーが戻ってこない（UIPI で破棄等）場合でも、ずっと送り直し続けない
        _o.OnSent(3, 1000);
        Assert.True(_o.ShouldReinject(1000 + InjectionOrder.TimeoutMs));
        Assert.False(_o.ShouldReinject(1001 + InjectionOrder.TimeoutMs));
        Assert.Equal(0, _o.Pending);
    }

    [Fact]
    public void FailedSend_DoesNotWait()
    {
        _o.OnSent(0, 1000);
        Assert.False(_o.ShouldReinject(1001));
    }

    [Fact]
    public void ExtraSeenEvents_DoNotGoNegative()
    {
        _o.OnOwnInjectedSeen();
        _o.OnSent(1, 1000);
        Assert.True(_o.ShouldReinject(1001));
    }
}
