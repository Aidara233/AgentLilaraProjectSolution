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
        var engine = TestHost.BuildSessionEngineWithFakeSession("S1", alive: true);

        var taken = engine.TakeOverChannelPool(1, "S1"); // 无占用

        Assert.False(taken);
        Assert.Null(engine.GetFullClaimer(1));
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
        var engine = TestHost.BuildSessionEngineWithFakeSession("S1", alive: true);

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
}
