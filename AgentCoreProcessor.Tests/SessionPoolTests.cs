// AgentCoreProcessor.Tests/SessionPoolTests.cs
using AgentCoreProcessor.Engine.Session;
using AgentLilara.PluginSDK;
using Xunit;

namespace AgentCoreProcessor.Tests;

/// <summary>
/// 资源池频道占用语义测试：占用状态在池里，escalate/compact 同池换活跃 session，claim 原地不动。
/// </summary>
public class SessionPoolTests
{
    [Fact]
    public void ClaimChannel_Full_returns_claimer()
    {
        var engine = TestHost.BuildSessionEngineWithFakeSession("S1", alive: true);

        var claimed = engine.ClaimChannel("S1", 1, ClaimLevel.Full);

        Assert.True(claimed);
        Assert.Equal("S1", engine.GetFullClaimer(1));
        Assert.True(engine.IsChannelClaimed(1));
        Assert.True(engine.HasActiveWorking(1));
    }

    [Fact]
    public void ClaimChannel_Full_conflict_rejected()
    {
        var engine = TestHost.BuildSessionEngineWithFakeSession("S1", alive: true);
        engine.ClaimChannel("S1", 1, ClaimLevel.Full);

        var second = engine.ClaimChannel("S2", 1, ClaimLevel.Full);

        Assert.False(second);
        Assert.Equal("S1", engine.GetFullClaimer(1)); // 占用未被顶掉
    }

    [Fact]
    public void TakeOver_keeps_claim_swaps_active_session()
    {
        // escalate/compact 语义：express → working 共享同一池，claim 原地不动
        var engine = TestHost.BuildSessionEngineWithFakeSession("express", alive: true);
        engine.ClaimChannel("express", 1, ClaimLevel.Full);

        var taken = engine.TakeOverChannelPool(1, "working");

        Assert.True(taken);
        Assert.Equal("working", engine.GetFullClaimer(1));  // claim 保持，活跃 session 换 working
        Assert.True(engine.IsChannelClaimed(1));
    }

    [Fact]
    public void TakeOver_unclaimed_pool_rejected()
    {
        var engine = TestHost.BuildSessionEngineWithFakeSession("S1", alive: true, registerInPool: false);

        var taken = engine.TakeOverChannelPool(1, "S1"); // 无占用

        Assert.False(taken);
        Assert.Null(engine.GetFullClaimer(1));
    }

    [Fact]
    public void Claim_after_takeover_is_idempotent()
    {
        // compact 场景：池 TakeOver 到新 session id 后，新 session connect_channel 幂等命中
        var engine = TestHost.BuildSessionEngineWithFakeSession("old", alive: true);
        engine.ClaimChannel("old", 1, ClaimLevel.Full);

        engine.TakeOverChannelPool(1, "new"); // compact 换活跃 session，claim 原地不动

        // 新 session 的 connect_channel 走 ClaimChannel → 幂等成功（不撞已有占用）
        var claimed = engine.ClaimChannel("new", 1, ClaimLevel.Full);

        Assert.True(claimed);
        Assert.Equal("new", engine.GetFullClaimer(1));
    }

    [Fact]
    public void ReleaseChannel_returns_unclaimed()
    {
        var engine = TestHost.BuildSessionEngineWithFakeSession("S1", alive: true);
        engine.ClaimChannel("S1", 1, ClaimLevel.Full);

        engine.ReleaseChannel("S1", 1);

        Assert.Null(engine.GetFullClaimer(1));
        Assert.False(engine.IsChannelClaimed(1));
        Assert.False(engine.HasActiveWorking(1));
    }

    [Fact]
    public void Multiple_listen_sessions_coexist()
    {
        var engine = TestHost.BuildSessionEngineWithFakeSession("S1", alive: true, registerInPool: false);

        Assert.True(engine.ClaimChannel("S1", 1, ClaimLevel.Listen));
        Assert.True(engine.ClaimChannel("S2", 1, ClaimLevel.Listen));
        // Listen 不占用 Full
        Assert.Null(engine.GetFullClaimer(1));
        Assert.False(engine.IsChannelClaimed(1));

        // Listen 与 Full 可并存
        Assert.True(engine.ClaimChannel("S3", 1, ClaimLevel.Full));
        Assert.Equal("S3", engine.GetFullClaimer(1));
    }

    [Fact]
    public void Release_other_session_does_not_release_my_claim()
    {
        var engine = TestHost.BuildSessionEngineWithFakeSession("S1", alive: true);
        engine.ClaimChannel("S1", 1, ClaimLevel.Full);

        engine.ReleaseChannel("S2", 1); // 非持有者释放

        Assert.Equal("S1", engine.GetFullClaimer(1)); // claim 保留
    }

    // ── 资源池树（子 agent 语义）──

    [Fact]
    public void SubPool_child_resolves_to_direct_parent()
    {
        // working 占频道（顶层池），子 agent 建子池挂父池
        var engine = TestHost.BuildSessionEngineWithFakeSession("working", alive: true); // channel 1

        engine.CreateSubPool("child", "working");

        Assert.Equal("working", engine.ResolveEffectiveParent("child")); // 直接父
        Assert.Equal("session:child", engine.ResolveActiveSession("session:child")); // 子 agent 是自己池的活跃
    }

    [Fact]
    public void SubPool_grandchild_parent_is_child_not_top()
    {
        // 孙 agent：父是子 agent，不是顶层 working
        var engine = TestHost.BuildSessionEngineWithFakeSession("working", alive: true);
        engine.CreateSubPool("child", "working");

        engine.CreateSubPool("grandchild", "child");

        Assert.Equal("child", engine.ResolveEffectiveParent("grandchild")); // 直接父 = child
    }

    [Fact]
    public void SubPool_parent_compact_child_follows_new_parent()
    {
        // 父 working compact → 顶层池换活跃 session，子 agent 找父自动跟随新 working
        var engine = TestHost.BuildSessionEngineWithFakeSession("working", alive: true); // channel 1
        engine.CreateSubPool("child", "working");

        engine.TakeOverChannelPool(1, "working2");

        Assert.Equal("working2", engine.ResolveEffectiveParent("child"));
        // 子 agent 仍是自己池的活跃，未受影响
        Assert.Equal("session:child", engine.ResolveActiveSession("session:child"));
    }

    [Fact]
    public void SubPool_release_child_removes_from_parent()
    {
        var engine = TestHost.BuildSessionEngineWithFakeSession("working", alive: true);
        engine.CreateSubPool("child", "working");

        engine.ReleaseSessionClaims("child");

        Assert.Null(engine.ResolveEffectiveParent("child")); // 子池已从父移除
    }
}
