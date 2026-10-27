using AgentCoreProcessor.Engine.Session;
using AgentLilara.PluginSDK;
using Xunit;

namespace AgentCoreProcessor.Tests;

public sealed class TakeoverIdConflictTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void RootTakeoverRejectsIdIndexedToAnotherRootOrChild(bool channelEntry, bool targetChild)
    {
        var engine = TestHost.BuildSessionEngineWithFakeSession("source", true);
        engine.CreateSubPool("source-child", "source");
        if (targetChild) engine.CreateSubPool("target", "source");
        else engine.ClaimChannel("target", 2, ClaimLevel.Full);
        AssertRejectedWithoutChanges(engine, "source", "target", () => channelEntry
            ? engine.TakeOverChannelPool(1, "target") : engine.TakeOverSessionPool("source", "target"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChildTakeoverRejectsIdIndexedToAnotherPool(bool targetChild)
    {
        var engine = TestHost.BuildSessionEngineWithFakeSession("parent", true);
        engine.CreateSubPool("source", "parent");
        engine.CreateSubPool("grandchild", "source");
        if (targetChild) engine.CreateSubPool("target", "parent");
        else engine.ClaimChannel("target", 2, ClaimLevel.Full);
        AssertRejectedWithoutChanges(engine, "source", "target", () => engine.TakeOverSessionPool("source", "target"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RootTakeoverAlsoProtectsListenOnlyIndex(bool channelEntry)
    {
        var engine = TestHost.BuildSessionEngineWithFakeSession("source", true);
        engine.ClaimChannel("target", 2, ClaimLevel.Listen);
        AssertRejectedWithoutChanges(engine, "source", "target", () => channelEntry
            ? engine.TakeOverChannelPool(1, "target") : engine.TakeOverSessionPool("source", "target"));
    }

    private static void AssertRejectedWithoutChanges(SessionEngine engine, string sourceId, string targetId, Func<bool> takeover)
    {
        var pools = engine.GetPoolsSnapshot().ToArray();
        var states = pools.Select(pool => new
        {
            Pool = pool, pool.ActiveSessionId, Members = pool.Members.OrderBy(x => x).ToArray(),
            Listeners = pool.ListenSessionIds.ToArray(), pool.Parent,
            Children = pool.ChildPools.ToArray(), pool.Occupancy
        }).ToArray();
        var ids = states.SelectMany(s => s.Members.Concat(s.Listeners)).Distinct().ToArray();
        var indexes = ids.ToDictionary(id => id, engine.GetPoolBySession);
        var source = engine.GetPoolBySession(sourceId);
        var target = engine.GetPoolBySession(targetId);
        Assert.NotSame(source, target);
        Assert.False(takeover());
        Assert.Equal(pools, engine.GetPoolsSnapshot());
        foreach (var state in states)
        {
            Assert.Equal(state.ActiveSessionId, state.Pool.ActiveSessionId);
            Assert.Equal(state.Members, state.Pool.Members.OrderBy(x => x));
            Assert.Equal(state.Listeners, state.Pool.ListenSessionIds);
            Assert.Same(state.Parent, state.Pool.Parent);
            Assert.Equal(state.Children, state.Pool.ChildPools);
            Assert.Same(state.Occupancy, state.Pool.Occupancy);
        }
        foreach (var pair in indexes) Assert.Same(pair.Value, engine.GetPoolBySession(pair.Key));
        Assert.Same(source, engine.GetPoolBySession(sourceId));
        Assert.Same(target, engine.GetPoolBySession(targetId));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void SamePoolHistoryAndCurrentIdRemainValid(bool channelEntry, bool child)
    {
        var engine = TestHost.BuildSessionEngineWithFakeSession("root", true);
        var old = "root";
        if (child) { engine.CreateSubPool("child", "root"); old = "child"; }
        var pool = engine.GetPoolBySession(old)!;
        bool Take(string id) => channelEntry ? engine.TakeOverChannelPool(1, id) : engine.TakeOverSessionPool(old, id);
        Assert.True(Take("next"));
        Assert.True(Take("next"));
        Assert.True(Take(old));
        Assert.Same(pool, engine.GetPoolBySession("next"));
        Assert.Equal(old, engine.ResolveActiveSession("next"));
        Assert.Equal(old, pool.ActiveSessionId);
        Assert.True(pool.Members.SetEquals(new[] { old, "next" }));
    }

    [Fact]
    public void MissingSourceAndUnclaimedChannelRemainFalseWithoutTouchingTarget()
    {
        var engine = TestHost.BuildSessionEngineWithFakeSession("target", true);
        var target = engine.GetPoolBySession("target");
        engine.ClaimChannel("listener", 2, ClaimLevel.Listen);
        Assert.False(engine.TakeOverSessionPool("missing", "target"));
        Assert.False(engine.TakeOverChannelPool(99, "target"));
        Assert.False(engine.TakeOverChannelPool(2, "target"));
        Assert.Same(target, engine.GetPoolBySession("target"));
        Assert.Equal("target", engine.GetFullClaimer(1));
        Assert.Equal(new[] { "listener" }, engine.GetPoolBySession("listener")!.ListenSessionIds);
    }
}
