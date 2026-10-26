namespace Plugin.Email;

/// <summary>邮箱游标（落盘形状）。<c>UidValidity</c> 变了意味着旧 UID 全部失效。</summary>
public sealed class MailCursor
{
    public uint UidValidity { get; set; }

    public long LastUid { get; set; }
}

/// <summary>本轮该怎么处理游标。</summary>
public enum CursorAction
{
    /// <summary>从未同步过：只记基线，不投递历史。</summary>
    PrimeBaseline,

    /// <summary>UIDVALIDITY 变了：旧 UID 全部失效，重置基线且不投递。</summary>
    ResetBaseline,

    /// <summary>正常：投递游标之后的新 UID。</summary>
    Deliver,
}

/// <summary>一次决策的结果：动作 + 新游标 + 该投哪些 UID（已排序、已去重）。</summary>
public sealed record CursorDecision(
    CursorAction Action,
    long NewLastUid,
    IReadOnlyList<long> Uids);

/// <summary>
/// 邮件游标决策。**纯函数，不碰 IMAP**——这样"重放历史"与"永久漏信"这两类最难排查的
/// bug 才可能被测试挡住，而不是只能靠真连一个邮箱去试。
/// <para>三条规则：</para>
/// <list type="number">
/// <item>没有游标 → 只记基线，不投递：装好插件那一刻不该把积压的几百封未读全当成"刚到的"。</item>
/// <item>UIDVALIDITY 变了 → 重置基线，不投递：IMAP 明确允许服务器这么做，此时旧 UID 全部无效，
/// 拿旧游标去比会得出完全错误的结论。</item>
/// <item>正常 → 只投 UID &gt; 游标的，而**新游标取本轮见过的最大 UID**，
/// 不是"成功投递的最大 UID"：某封解析失败也必须越过它，否则会永久卡在这一封上，
/// 每次轮询都重试同一封、后面的信永远收不到。</item>
/// </list>
/// </summary>
public static class MailCursorPolicy
{
    public static CursorDecision Decide(
        MailCursor? stored,
        uint currentUidValidity,
        long baselineUid,
        IReadOnlyList<long> candidates)
    {
        var safeBaseline = baselineUid;
        if (candidates.Count > 0)
        {
            var maxCandidate = candidates.Max();
            if (maxCandidate > safeBaseline)
            {
                safeBaseline = maxCandidate;
            }
        }

        if (stored is null)
        {
            return new CursorDecision(CursorAction.PrimeBaseline, safeBaseline, Array.Empty<long>());
        }

        if (stored.UidValidity != currentUidValidity)
        {
            return new CursorDecision(CursorAction.ResetBaseline, safeBaseline, Array.Empty<long>());
        }

        var fresh = candidates
            .Where(uid => uid > stored.LastUid)
            .Distinct()
            .OrderBy(uid => uid)
            .ToList();

        // 即使 fresh 为空，游标也要吃掉"见过但比 lastUid 大"的部分——
        // 那是解析失败被跳过的那些。
        return new CursorDecision(CursorAction.Deliver, safeBaseline, fresh);
    }
}
