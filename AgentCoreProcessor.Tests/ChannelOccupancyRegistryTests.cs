using System.Reflection;
using AgentCoreProcessor.Engine;
using AgentCoreProcessor.Engine.Session;
using AgentLilara.PluginSDK;
using Xunit;

namespace AgentCoreProcessor.Tests;

public sealed class ChannelOccupancyRegistryTests
{
    private static ChannelOccupancyRegistry Registry(SessionEngine engine) =>
        (ChannelOccupancyRegistry)typeof(SessionEngine).GetField("_occupancies", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(engine)!;

    [Fact]
    public void RemovalRequiresExactReferenceAndRecreationUsesFreshState()
    {
        var registry = new ChannelOccupancyRegistry();
        var first = registry.GetOrCreate(42);
        Assert.Same(first, registry.GetOrCreate(42));
        Assert.False(registry.Remove(42, new ChannelOccupancyState()));
        Assert.True(registry.Remove(42, first));
        var second = registry.GetOrCreate(42);
        Assert.NotSame(first, second);
        Assert.False(registry.Remove(42, first));
        Assert.True(registry.TryGet(42, out var current));
        Assert.Same(second, current);
    }

    [Fact]
    public void RootSharesRegistryStateAndListenerRetainsItUntilPoolIsEmpty()
    {
        var engine = TestHost.BuildSessionEngineWithFakeSession("owner", true);
        var registry = Registry(engine);
        var pool = engine.GetPoolBySession("owner")!;
        Assert.True(registry.TryGet(1, out var first));
        Assert.Same(first, pool.Occupancy);
        engine.ClaimChannel("listener", 1, ClaimLevel.Listen);
        engine.ReleaseChannel("owner", 1);
        Assert.True(registry.TryGet(1, out var retained));
        Assert.Same(first, retained);
        Assert.Equal(new[] { "listener" }, retained!.ListenSessionIds);
        engine.ReleaseChannel("listener", 1);
        Assert.False(registry.TryGet(1, out _));
        engine.ClaimChannel("next", 1, ClaimLevel.Full);
        Assert.True(registry.TryGet(1, out var recreated));
        Assert.NotSame(first, recreated);
        Assert.Same(recreated, engine.GetPoolBySession("next")!.Occupancy);
        first!.Listen("stale-listener");
        Assert.Empty(recreated!.ListenSessionIds);
        Assert.Equal("next", recreated.ActiveSessionId);
    }

    [Fact]
    public void LastChildCompletionReclaimsUnclaimedRootAndHistoricalIndexes()
    {
        var engine = TestHost.BuildSessionEngineWithFakeSession("old", true);
        engine.TakeOverSessionPool("old", "owner");
        engine.CreateSubPool("child-old", "owner");
        engine.TakeOverSessionPool("child-old", "child");
        engine.ReleaseChannel("owner", 1);
        Assert.True(Registry(engine).TryGet(1, out var previous));
        engine.OnChildCompleted("child");
        Assert.False(Registry(engine).TryGet(1, out _));
        Assert.Empty(engine.GetPoolsSnapshot());
        foreach (var id in new[] { "old", "owner", "child-old", "child" })
            Assert.Null(engine.GetPoolBySession(id));
        engine.OnChildCompleted("child");
        engine.ClaimChannel("new", 1, ClaimLevel.Full);
        Assert.True(Registry(engine).TryGet(1, out var recreated));
        Assert.NotSame(previous, recreated);
    }

    [Theory]
    [InlineData("listener")]
    [InlineData("child")]
    [InlineData("full")]
    public void ChildCompletionPreservesNonemptyRoot(string remaining)
    {
        var engine = TestHost.BuildSessionEngineWithFakeSession("owner", true);
        engine.CreateSubPool("done", "owner");
        var pool = engine.GetPoolBySession("owner")!;
        var parent = new RuntimeSession(new CreateSessionSpec { Type = "channel" }, "owner", new[] { "basic-tools" });
        parent.AddChild("done");
        var sessions = (System.Collections.Concurrent.ConcurrentDictionary<string, RuntimeSession>)typeof(SessionEngine)
            .GetField("_sessions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(engine)!;
        sessions["owner"] = parent;
        if (remaining == "listener") engine.ClaimChannel("listener", 1, ClaimLevel.Listen);
        if (remaining == "child") engine.CreateSubPool("other", "owner");
        if (remaining != "full") engine.ReleaseChannel("owner", 1);
        engine.OnChildCompleted("done");
        Assert.True(Registry(engine).TryGet(1, out var retained));
        Assert.Same(pool.Occupancy, retained);
        Assert.Same(pool, engine.GetPoolBySession("owner"));
        Assert.Null(engine.GetPoolBySession("done"));
        if (remaining == "full") Assert.DoesNotContain("done", parent.ChildSessionIds);
        if (remaining == "child")
        {
            Assert.NotNull(engine.GetPoolBySession("other"));
            engine.OnChildCompleted("other");
            Assert.False(Registry(engine).TryGet(1, out _));
        }
    }

    [Fact]
    public void GrandchildCompletionDoesNotReclaimActiveParentIdentity()
    {
        var engine = TestHost.BuildSessionEngineWithFakeSession("root", true);
        engine.CreateSubPool("parent", "root");
        engine.CreateSubPool("done", "parent");
        var parent = engine.GetPoolBySession("parent");
        engine.OnChildCompleted("done");
        Assert.Same(parent, engine.GetPoolBySession("parent"));
        Assert.Equal("parent", engine.ResolveActiveSession("parent"));
        Assert.Equal("root", engine.ResolveEffectiveParent("parent"));
        Assert.Empty(parent!.ChildPools);
        Assert.Null(engine.GetPoolBySession("done"));
    }

    [Fact]
    public void ChildPoolRetainsRootOccupancyAfterOwnerRelease()
    {
        var engine = TestHost.BuildSessionEngineWithFakeSession("owner", true);
        engine.CreateSubPool("child", "owner");
        var registry = Registry(engine);
        Assert.True(registry.TryGet(1, out var state));
        engine.ReleaseChannel("owner", 1);
        Assert.True(registry.TryGet(1, out var retained));
        Assert.Same(state, retained);
        Assert.False(retained!.HasFullClaim);
        engine.ClaimChannel("next", 1, ClaimLevel.Full);
        Assert.Same(state, engine.GetPoolBySession("next")!.Occupancy);
        Assert.Null(engine.GetPoolBySession("child")!.Occupancy);
    }
}
