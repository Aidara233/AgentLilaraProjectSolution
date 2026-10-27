using AgentLilara.PluginSDK;
using Xunit;

namespace AgentCoreProcessor.Tests;

public sealed class PoolMemberIndexGuardTests
{
    [Fact]
    public void RootReclamationPreservesHistoricalMemberRemappedToAnotherPool()
    {
        var engine = TestHost.BuildSessionEngineWithFakeSession("historical", true);
        var old = engine.GetPoolBySession("historical")!;
        engine.TakeOverSessionPool("historical", "owner");
        engine.ClaimChannel("historical", 2, ClaimLevel.Full);
        var current = engine.GetPoolBySession("historical")!;
        engine.ReleaseChannel("owner", 1);
        Assert.DoesNotContain(old, engine.GetPoolsSnapshot());
        Assert.Null(engine.GetPoolBySession("owner"));
        Assert.Same(current, engine.GetPoolBySession("historical"));
        Assert.Equal("historical", engine.ResolveActiveSession("historical"));
        Assert.Equal("historical", engine.GetFullClaimer(2));
        Assert.Single(engine.GetPoolsSnapshot());
    }

    [Fact]
    public void CompletedChildPoolPreservesHistoricalMemberRemappedToRoot()
    {
        var engine = TestHost.BuildSessionEngineWithFakeSession("parent", true);
        engine.CreateSubPool("historical-child", "parent");
        engine.TakeOverSessionPool("historical-child", "completed-child");
        var old = engine.GetPoolBySession("completed-child")!;
        engine.ClaimChannel("historical-child", 2, ClaimLevel.Full);
        var current = engine.GetPoolBySession("historical-child")!;
        // No active parent RuntimeSession is needed for this pool cleanup scenario.
        engine.ReleaseChannel("parent", 1);
        engine.OnChildCompleted("completed-child");
        Assert.Null(engine.GetPoolBySession("completed-child"));
        Assert.Null(engine.GetPoolBySession("parent"));
        Assert.DoesNotContain(old, engine.GetPoolsSnapshot());
        Assert.Same(current, engine.GetPoolBySession("historical-child"));
        Assert.Equal("historical-child", engine.GetFullClaimer(2));
        Assert.Equal("historical-child", engine.ResolveActiveSession("historical-child"));
        Assert.Single(engine.GetPoolsSnapshot());
    }
}
