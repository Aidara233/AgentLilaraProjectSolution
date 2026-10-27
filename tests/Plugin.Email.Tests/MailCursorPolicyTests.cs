using Plugin.Email;
using Xunit;

namespace AgentCoreProcessor.Tests;

/// <summary>
/// G1：邮件游标决策。
/// <para>这段逻辑的两个失败模式都很难查：**重放历史**（装好插件，积压的未读全部当成新邮件，
/// 一次性订阅一次全引爆）与**永久漏信**（某封解析失败后每次都卡在同一封，后面的信永远收不到）。
/// 抽成纯函数就是为了让这两类 bug 能被断言，而不是只能真连一个邮箱去试。</para>
/// </summary>
public class MailCursorPolicyTests
{
    [Fact]
    public void NoCursorOnlyTakesBaseline()
    {
        var decision = MailCursorPolicy.Decide(
            stored: null,
            currentUidValidity: 100,
            baselineUid: 5000,
            candidates: Array.Empty<long>());

        Assert.Equal(CursorAction.PrimeBaseline, decision.Action);
        Assert.Empty(decision.Uids);          // 首轮绝不投递历史
        Assert.Equal(5000, decision.NewLastUid);
    }

    [Fact]
    public void BaselineNeverGoesBackwardsAgainstCandidates()
    {
        // 服务器 UidNext 与候选偶尔不一致（同一次连接里刚好进了新信）
        var decision = MailCursorPolicy.Decide(
            stored: null,
            currentUidValidity: 100,
            baselineUid: 10,
            candidates: new long[] { 11, 12 });

        Assert.Equal(12, decision.NewLastUid);
        Assert.Empty(decision.Uids);
    }

    [Fact]
    public void UidValidityChangeResetsWithoutDelivering()
    {
        var stored = new MailCursor { UidValidity = 100, LastUid = 5000 };

        var decision = MailCursorPolicy.Decide(
            stored,
            currentUidValidity: 200,   // 服务器重建了邮箱
            baselineUid: 30,
            candidates: new long[] { 25, 30 });

        Assert.Equal(CursorAction.ResetBaseline, decision.Action);
        Assert.Empty(decision.Uids);           // 旧 UID 与新 UID 不可比，这一轮一律不投
        Assert.Equal(30, decision.NewLastUid);
    }

    [Fact]
    public void OnlyUidsNewerThanCursorAreDelivered()
    {
        var stored = new MailCursor { UidValidity = 100, LastUid = 5000 };

        var decision = MailCursorPolicy.Decide(
            stored,
            currentUidValidity: 100,
            baselineUid: 5003,
            candidates: new long[] { 4999, 5000, 5001, 5002, 5003 });

        Assert.Equal(CursorAction.Deliver, decision.Action);
        Assert.Equal(new long[] { 5001, 5002, 5003 }, decision.Uids); // 已过滤、已排序
        Assert.Equal(5003, decision.NewLastUid);
    }

    [Fact]
    public void EmptyCandidatesKeepTheCursor()
    {
        var stored = new MailCursor { UidValidity = 100, LastUid = 5000 };

        var decision = MailCursorPolicy.Decide(
            stored, 100, baselineUid: 5000, candidates: Array.Empty<long>());

        Assert.Equal(CursorAction.Deliver, decision.Action);
        Assert.Empty(decision.Uids);
        Assert.Equal(5000, decision.NewLastUid);
    }

    [Fact]
    public void CursorAdvancesPastFailedMessages()
    {
        // 模拟：5001 解析失败被跳过，5002 成功。游标必须越过 5001，
        // 否则每次都重试同一封、5003 及以后永远收不到。
        var stored = new MailCursor { UidValidity = 100, LastUid = 5000 };

        var decision = MailCursorPolicy.Decide(
            stored, 100, baselineUid: 5002, candidates: new long[] { 5001, 5002 });

        Assert.Equal(5002, decision.NewLastUid);
        // 决策层不知道谁解析失败，它只给出"该尝试哪些"，此处两个都在候选里
        Assert.Equal(new long[] { 5001, 5002 }, decision.Uids);
    }

    [Fact]
    public void DuplicateCandidatesAreCollapsed()
    {
        var stored = new MailCursor { UidValidity = 100, LastUid = 10 };

        var decision = MailCursorPolicy.Decide(
            stored, 100, baselineUid: 12, candidates: new long[] { 11, 11, 12, 12 });

        Assert.Equal(new long[] { 11, 12 }, decision.Uids);
    }
}
