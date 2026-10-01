namespace RdpImeHelper.Logic;

/// <summary>
/// 自分が SendInput したキーと、物理キーの順序を守る。
/// フックのコールバック内で SendInput したキーは、既に入力キューに並んでいる物理キーの後ろに入る。
/// RDP は複数のキーをまとめて送ってくるため、例えば Shift+2 の変換で
/// 「Shift 離す → @ → Shift 押し直す」を送ると、先に並んでいた物理的な Shift の離しに追い越され、
/// Shift が押しっぱなしになる（実機で確認）。
/// そこで、自分の送ったキーがまだフックに戻ってきていない間に届いた物理キーは、
/// 素通しせずに送り直して（＝自分の送ったキーの後ろに並べて）順序を保つ。
/// </summary>
internal sealed class InjectionOrder
{
    /// <summary>送ったキーが戻ってこない（UIPI で破棄された等）ときに待つのをやめるまでの時間。</summary>
    public const long TimeoutMs = 1000;

    private int _pending;
    private long _lastSentAt;

    /// <summary>送ったがまだフックで見ていないキーの数。</summary>
    public int Pending => _pending;

    /// <summary>SendInput したとき（count は実際に送れた数）。</summary>
    public void OnSent(int count, long now)
    {
        if (count > 0)
        {
            _pending += count;
            _lastSentAt = now;
        }
    }

    /// <summary>自分が送ったキー（目印付きの injected）がフックに来たとき。</summary>
    public void OnOwnInjectedSeen()
    {
        if (_pending > 0)
        {
            _pending--;
        }
    }

    /// <summary>素通しするはずの物理キーを、送り直すべきか。</summary>
    public bool ShouldReinject(long now)
    {
        if (_pending == 0)
        {
            return false;
        }

        if (now - _lastSentAt > TimeoutMs)
        {
            _pending = 0;
            return false;
        }

        return true;
    }
}
