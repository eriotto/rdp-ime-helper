namespace RdpImeHelper.Logic;

internal enum LayoutKind
{
    /// <summary>スキャンコード 0x1A が '@'。</summary>
    Jis,

    /// <summary>スキャンコード 0x1A が '['。</summary>
    Us,

    /// <summary>それ以外（取得失敗など）。</summary>
    Unknown,
}

internal enum LayoutProblem
{
    None,

    /// <summary>HKL が取得できなかった（0）。コンソールウィンドウが前面のときなどに起きる。判定しない。</summary>
    Undetermined,

    /// <summary>パターンA：HKL が日本語以外。LoadKeyboardLayout で戻す。</summary>
    NonJapanese,

    /// <summary>パターンB：HKL は日本語だが JIS として振る舞わない（101 配列など）。通知のみ。</summary>
    JapaneseNotJis,
}

internal readonly record struct LayoutJudgement(nint Hkl, char Char, LayoutKind Kind, bool IsJapanese)
{
    public const int LangIdJapanese = 0x0411;

    public LayoutProblem Problem =>
        Hkl == 0 ? LayoutProblem.Undetermined
        : !IsJapanese ? LayoutProblem.NonJapanese
        : Kind != LayoutKind.Jis ? LayoutProblem.JapaneseNotJis
        : LayoutProblem.None;

    public bool IsJis => Problem == LayoutProblem.None;

    /// <summary>HKL と、その HKL でスキャンコード 0x1A が表す文字から判定する。</summary>
    public static LayoutJudgement Judge(nint hkl, char charOfScan1A)
    {
        var kind = charOfScan1A switch
        {
            '@' => LayoutKind.Jis,
            '[' => LayoutKind.Us,
            _ => LayoutKind.Unknown,
        };

        // HKL の下位ワードが入力言語（LANGID）
        bool japanese = (hkl & 0xFFFF) == LangIdJapanese;
        return new LayoutJudgement(hkl, charOfScan1A, kind, japanese);
    }

    public override string ToString() =>
        $"HKL=0x{(ulong)Hkl:X8} 0x1A='{(Char == '\0' ? "\\0" : Char.ToString())}' 判定={Kind}"
        + (Problem == LayoutProblem.None ? "" : $"（{Problem}）");
}

/// <summary>HKL ごとの判定結果をキャッシュする。</summary>
internal sealed class LayoutDetector
{
    private readonly Dictionary<nint, LayoutJudgement> _cache = new();

    /// <param name="charOfScan1A">キャッシュに無いときだけ呼ばれる（MapVirtualKeyEx）。</param>
    /// <param name="isNew">キャッシュに無く、今回判定したとき true。</param>
    public LayoutJudgement Detect(nint hkl, Func<nint, char> charOfScan1A, out bool isNew)
    {
        if (_cache.TryGetValue(hkl, out var cached))
        {
            isNew = false;
            return cached;
        }

        var judgement = LayoutJudgement.Judge(hkl, charOfScan1A(hkl));
        _cache[hkl] = judgement;
        isNew = true;
        return judgement;
    }

    /// <summary>接続ごとに呼ぶ（同じ HKL でも接続先のキーボード種別で振る舞いが変わりうるため）。</summary>
    public void Clear() => _cache.Clear();
}

internal enum CorrectionAction
{
    None,

    /// <summary>パターンA の是正（LoadKeyboardLayout + WM_INPUTLANGCHANGEREQUEST）を UI スレッドで行う。</summary>
    CorrectLayout,

    /// <summary>パターンB：トレイ通知のみ。</summary>
    NotifyJapaneseNotJis,

    /// <summary>是正に失敗した：トレイ通知のみ。</summary>
    NotifyCorrectionFailed,
}

/// <summary>是正の方針。是正・通知は接続1回につき1回まで（是正が効かない環境での無限ループ防止）。</summary>
internal sealed class CorrectionPolicy
{
    private bool _corrected;
    private bool _notifiedNotJis;

    public void OnConnected()
    {
        _corrected = false;
        _notifiedNotJis = false;
    }

    public CorrectionAction OnJudged(LayoutJudgement judgement, bool isRemoteSession)
    {
        if (!isRemoteSession)
        {
            // ローカルの配列はユーザーの設定なので触らない
            return CorrectionAction.None;
        }

        switch (judgement.Problem)
        {
            case LayoutProblem.NonJapanese when !_corrected:
                _corrected = true;
                return CorrectionAction.CorrectLayout;
            case LayoutProblem.JapaneseNotJis when !_notifiedNotJis:
                _notifiedNotJis = true;
                return CorrectionAction.NotifyJapaneseNotJis;
            default:
                return CorrectionAction.None;
        }
    }

    public CorrectionAction OnCorrectionResult(LayoutJudgement after) =>
        after.IsJis || after.Problem == LayoutProblem.Undetermined
            ? CorrectionAction.None
            : CorrectionAction.NotifyCorrectionFailed;
}

/// <summary>US→JIS 変換の有効条件。</summary>
internal static class ConversionCondition
{
    /// <summary>GetKeyboardType(0) の日本語キーボード（106/109）。RDP では接続元が申告した値。</summary>
    public const int KeyboardTypeJapanese = 7;

    /// <summary>
    /// RDP セッション かつ 配列が JIS かつ 接続元のキーボードが日本語以外（英語配列など）。
    /// 接続元が JIS キーボードなら、JIS 配列のセッションでそのまま正しく入力できるので変換しない。
    /// </summary>
    public static bool IsMet(bool isRemoteSession, LayoutJudgement? layout, int clientKeyboardType) =>
        isRemoteSession && layout is { IsJis: true } && clientKeyboardType != KeyboardTypeJapanese;
}
