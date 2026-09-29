using RdpImeHelper.Logic;

namespace RdpImeHelper.Tests;

public class LayoutJudgementTests
{
    private const nint JapaneseJis = 0x04110411;
    // x64 の HKL は 32bit 値の符号拡張（例：0xFFFFFFFFE0010411）
    private static readonly nint JapaneseIme = unchecked((nint)(int)0xE0010411);
    private const nint EnglishUs = 0x04090409;
    private static readonly nint EnglishUsOnJapaneseLang = unchecked((nint)(int)0xF0200411);

    [Theory]
    [InlineData(0x04110411L, '@')]
    [InlineData(0xE0010411L, '@')]
    public void JapaneseHkl_WithAt_IsJis(long hkl, char c)
    {
        var j = LayoutJudgement.Judge((nint)hkl, c);
        Assert.Equal(LayoutKind.Jis, j.Kind);
        Assert.True(j.IsJapanese);
        Assert.Equal(LayoutProblem.None, j.Problem);
        Assert.True(j.IsJis);
    }

    [Fact]
    public void JapaneseHkl_WithBracket_IsPatternB()
    {
        var j = LayoutJudgement.Judge(JapaneseJis, '[');
        Assert.Equal(LayoutKind.Us, j.Kind);
        Assert.Equal(LayoutProblem.JapaneseNotJis, j.Problem);
        Assert.False(j.IsJis);
    }

    [Fact]
    public void JapaneseLanguageWithUsLayoutId_IsPatternB()
    {
        // 日本語入力言語 + 英語キーボード（HKL の上位ワードが別レイアウト）
        var j = LayoutJudgement.Judge(EnglishUsOnJapaneseLang, '[');
        Assert.Equal(LayoutProblem.JapaneseNotJis, j.Problem);
    }

    [Theory]
    [InlineData('[')]
    [InlineData('@')]
    [InlineData('\0')]
    public void NonJapaneseHkl_IsPatternA_RegardlessOfChar(char c)
    {
        var j = LayoutJudgement.Judge(EnglishUs, c);
        Assert.False(j.IsJapanese);
        Assert.Equal(LayoutProblem.NonJapanese, j.Problem);
        Assert.False(j.IsJis);
    }

    [Theory]
    [InlineData('\0')]
    [InlineData('ü')]
    public void JapaneseHkl_WithUnknownChar_IsNotJis(char c)
    {
        var j = LayoutJudgement.Judge(JapaneseIme, c);
        Assert.Equal(LayoutKind.Unknown, j.Kind);
        Assert.Equal(LayoutProblem.JapaneseNotJis, j.Problem);
    }

    [Fact]
    public void ToString_ContainsHklAndResult()
    {
        string s = LayoutJudgement.Judge(EnglishUs, '[').ToString();
        Assert.Contains("HKL=0x04090409", s);
        Assert.Contains("Us", s);
        Assert.Contains("NonJapanese", s);
        Assert.Contains("E0010411", LayoutJudgement.Judge(JapaneseIme, '@').ToString());
    }
}

public class LayoutDetectorTests
{
    private readonly LayoutDetector _d = new();
    private readonly List<nint> _calls = new();

    private char Map(nint hkl)
    {
        _calls.Add(hkl);
        return (hkl & 0xFFFF) == 0x0411 ? '@' : '[';
    }

    [Fact]
    public void SameHkl_IsMappedOnce()
    {
        var first = _d.Detect(0x04110411, Map, out bool isNew1);
        var second = _d.Detect(0x04110411, Map, out bool isNew2);
        Assert.True(isNew1);
        Assert.False(isNew2);
        Assert.Equal(first, second);
        Assert.Single(_calls);
    }

    [Fact]
    public void DifferentHkls_AreCachedSeparately()
    {
        Assert.True(_d.Detect(0x04110411, Map, out _).IsJis);
        Assert.False(_d.Detect(0x04090409, Map, out _).IsJis);
        Assert.True(_d.Detect(0x04110411, Map, out _).IsJis);
        Assert.False(_d.Detect(0x04090409, Map, out _).IsJis);
        Assert.Equal(new nint[] { 0x04110411, 0x04090409 }, _calls);
    }

    [Fact]
    public void Clear_ForcesRemap()
    {
        // 同じ HKL でも再接続後は振る舞いが変わりうる（パターンB → 是正後の再ログオンなど）
        Assert.True(_d.Detect(0x04110411, _ => '[', out _).Problem == LayoutProblem.JapaneseNotJis);
        Assert.True(_d.Detect(0x04110411, _ => '@', out _).Problem == LayoutProblem.JapaneseNotJis);
        _d.Clear();
        Assert.True(_d.Detect(0x04110411, _ => '@', out bool isNew).IsJis);
        Assert.True(isNew);
    }
}

public class CorrectionPolicyTests
{
    private static readonly LayoutJudgement Jis = LayoutJudgement.Judge(0x04110411, '@');
    private static readonly LayoutJudgement PatternA = LayoutJudgement.Judge(0x04090409, '[');
    private static readonly LayoutJudgement PatternB = LayoutJudgement.Judge(0x04110411, '[');

    private readonly CorrectionPolicy _p = new();

    [Fact]
    public void Jis_NeedsNothing()
    {
        Assert.Equal(CorrectionAction.None, _p.OnJudged(Jis, isRemoteSession: true));
    }

    [Fact]
    public void PatternA_IsCorrected()
    {
        Assert.Equal(CorrectionAction.CorrectLayout, _p.OnJudged(PatternA, true));
    }

    [Fact]
    public void PatternA_IsCorrectedOnlyOncePerConnection()
    {
        Assert.Equal(CorrectionAction.CorrectLayout, _p.OnJudged(PatternA, true));
        // 是正が効かず、キー押下のたびに非日本語と判定され続けてもループしない
        for (int i = 0; i < 1000; i++)
        {
            Assert.Equal(CorrectionAction.None, _p.OnJudged(PatternA, true));
        }
    }

    [Fact]
    public void PatternA_AfterJisAgainInSameConnection_IsNotCorrectedAgain()
    {
        // 是正 → JIS に戻った → ユーザーが再び英語に切り替えた：同じ接続中は2回目の是正をしない
        Assert.Equal(CorrectionAction.CorrectLayout, _p.OnJudged(PatternA, true));
        Assert.Equal(CorrectionAction.None, _p.OnJudged(Jis, true));
        Assert.Equal(CorrectionAction.None, _p.OnJudged(PatternA, true));
    }

    [Fact]
    public void Reconnect_AllowsOneMoreCorrection()
    {
        Assert.Equal(CorrectionAction.CorrectLayout, _p.OnJudged(PatternA, true));
        Assert.Equal(CorrectionAction.None, _p.OnJudged(PatternA, true));

        _p.OnConnected();
        Assert.Equal(CorrectionAction.CorrectLayout, _p.OnJudged(PatternA, true));
        Assert.Equal(CorrectionAction.None, _p.OnJudged(PatternA, true));
    }

    [Fact]
    public void ConnectWithoutProblem_DoesNotConsumeTheCorrection()
    {
        _p.OnConnected();
        Assert.Equal(CorrectionAction.None, _p.OnJudged(Jis, true));
        Assert.Equal(CorrectionAction.CorrectLayout, _p.OnJudged(PatternA, true));
    }

    [Fact]
    public void PatternB_IsNotifiedOnlyOncePerConnection_AndNeverCorrected()
    {
        Assert.Equal(CorrectionAction.NotifyJapaneseNotJis, _p.OnJudged(PatternB, true));
        for (int i = 0; i < 100; i++)
        {
            Assert.Equal(CorrectionAction.None, _p.OnJudged(PatternB, true));
        }

        _p.OnConnected();
        Assert.Equal(CorrectionAction.NotifyJapaneseNotJis, _p.OnJudged(PatternB, true));
    }

    [Fact]
    public void PatternAAndB_HaveIndependentLimits()
    {
        Assert.Equal(CorrectionAction.NotifyJapaneseNotJis, _p.OnJudged(PatternB, true));
        Assert.Equal(CorrectionAction.CorrectLayout, _p.OnJudged(PatternA, true));
        Assert.Equal(CorrectionAction.None, _p.OnJudged(PatternB, true));
        Assert.Equal(CorrectionAction.None, _p.OnJudged(PatternA, true));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LocalSession_IsNeverCorrectedOrNotified(bool patternA)
    {
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(CorrectionAction.None, _p.OnJudged(patternA ? PatternA : PatternB, isRemoteSession: false));
        }

        // ローカルでの判定は RDP 接続時の1回分を消費しない
        Assert.NotEqual(CorrectionAction.None, _p.OnJudged(patternA ? PatternA : PatternB, isRemoteSession: true));
    }

    [Fact]
    public void CorrectionResult_Success_NeedsNothing()
    {
        Assert.Equal(CorrectionAction.None, _p.OnCorrectionResult(Jis));
    }

    [Theory]
    [InlineData(0x04090409L, '[')]
    [InlineData(0x04110411L, '[')]
    public void CorrectionResult_Failure_IsNotifiedOnly(long hkl, char c)
    {
        Assert.Equal(CorrectionAction.CorrectLayout, _p.OnJudged(PatternA, true));
        Assert.Equal(CorrectionAction.NotifyCorrectionFailed, _p.OnCorrectionResult(LayoutJudgement.Judge((nint)hkl, c)));
        // 失敗後も同じ接続中は再是正しない
        Assert.Equal(CorrectionAction.None, _p.OnJudged(PatternA, true));
    }
}
