namespace RdpImeHelper;

// 配列判定・是正。
// TODO: 現在はスタブ。RDP セッション判定（GetSystemMetrics(SM_REMOTESESSION)）と
//       JIS 判定（前面スレッドの HKL + MapVirtualKeyEx）を実装する。
internal static class LayoutMonitor
{
    /// <summary>US→JIS 変換の有効条件（RDP セッション かつ 配列が JIS）。</summary>
    public static bool IsConversionConditionMet() => IsRemoteSession() && IsJisLayout();

    // スタブ：常に false（トレイの「ローカルでも強制有効」でのみ変換が有効になる）
    public static bool IsRemoteSession() => false;

    // スタブ：常に false
    public static bool IsJisLayout() => false;
}
