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
