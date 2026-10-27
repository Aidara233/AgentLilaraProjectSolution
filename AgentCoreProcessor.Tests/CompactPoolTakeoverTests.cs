using AgentCoreProcessor.Engine.Session;
using AgentLilara.PluginSDK;
using Xunit;

namespace AgentCoreProcessor.Tests;

public sealed class CompactPoolTakeoverTests
{
    [Fact]
    public void RootTakeoverPublishesBothMemberReferencesAndKeepsChildren()
    {
        var engine = TestHost.BuildSessionEngineWithFakeSession("old", true);
        engine.CreateSubPool("child", "old");
        var pool = engine.GetPoolBySession("old");
        var child = engine.GetPoolBySession("child");
        Assert.True(engine.TakeOverSessionPool("old", "new"));
        Assert.Same(pool, engine.GetPoolBySession("new"));
        Assert.Equal("new", engine.ResolveActiveSession("old"));
        Assert.Equal("new", engine.ResolveActiveSession("new"));
        Assert.Equal("new", engine.ResolveEffectiveParent("child"));
        Assert.Same(pool, child!.Parent);
        Assert.Contains(child, pool!.ChildPools);
        Assert.Contains("old", pool.Members);
        Assert.Contains("new", pool.Members);
        engine.ReleaseChannel("old", 1);
        engine.ReleaseSessionClaims("old");
        Assert.Equal("new", engine.GetFullClaimer(1));
        Assert.True(engine.ClaimChannel("new", 1, ClaimLevel.Full));
        Assert.Same(pool, engine.GetPoolBySession("new"));
    }

    [Fact]
    public void ChildTakeoverPreservesParentAndGrandchildWithoutClaimingChannel()
    {
        var engine = TestHost.BuildSessionEngineWithFakeSession("parent", true);
        engine.CreateSubPool("old-child", "parent");
        engine.CreateSubPool("grandchild", "old-child");
        var pool = engine.GetPoolBySession("old-child");
        Assert.True(engine.TakeOverSessionPool("old-child", "new-child"));
        Assert.Same(pool, engine.GetPoolBySession("new-child"));
        Assert.Equal("new-child", engine.ResolveActiveSession("old-child"));
        Assert.Equal("new-child", engine.ResolveActiveSession("new-child"));
        Assert.Equal("parent", engine.ResolveEffectiveParent("new-child"));
        Assert.Equal("new-child", engine.ResolveEffectiveParent("grandchild"));
        Assert.False(pool!.HasFullClaim);
        Assert.Equal("parent", engine.GetFullClaimer(1));
        engine.ReleaseSessionClaims("old-child");
        Assert.Equal("new-child", pool.ActiveSessionId);
        Assert.Contains("grandchild", engine.GetActiveChildren("new-child")!);
    }

    [Fact]
    public void MissingPoolDoesNotCreateReservation()
    {
        var engine = TestHost.BuildSessionEngineWithFakeSession("old", true, registerInPool: false);
        Assert.False(engine.TakeOverSessionPool("old", "new"));
        Assert.Null(engine.GetPoolBySession("new"));
        Assert.Null(engine.ResolveActiveSession("new"));
        Assert.Empty(engine.GetPoolsSnapshot());
    }
}
