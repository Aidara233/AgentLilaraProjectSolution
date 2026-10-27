using AgentLilara.PluginSDK;
using Xunit;

namespace AgentCoreProcessor.Tests;

public sealed class ListenMemberIndexTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReleasingListenOnlyMemberRemovesItsIndex(bool retainPool)
    {
        var engine = TestHost.BuildSessionEngineWithFakeSession("owner", true, registerInPool: retainPool);
        engine.ClaimChannel("listener", 1, ClaimLevel.Listen);
        var pool = engine.GetPoolBySession("listener");
        Assert.NotNull(pool);
        Assert.DoesNotContain("listener", pool.Members);
        engine.ReleaseChannel("listener", 1);
        Assert.Null(engine.GetPoolBySession("listener"));
        Assert.Null(engine.ResolveActiveSession("listener"));
        Assert.Empty(pool.ListenSessionIds);
        if (retainPool)
        {
            Assert.Same(pool, engine.GetPoolBySession("owner"));
            Assert.Equal("owner", engine.GetFullClaimer(1));
        }
        else Assert.Empty(engine.GetPoolsSnapshot());
    }

    [Fact]
    public void FullMemberRetainsHistoricalIndexWhileAnotherListenerKeepsPoolAlive()
    {
        var engine = TestHost.BuildSessionEngineWithFakeSession("owner", true);
        engine.ClaimChannel("owner", 1, ClaimLevel.Listen);
        engine.ClaimChannel("other-listener", 1, ClaimLevel.Listen);
        var pool = engine.GetPoolBySession("owner");
        engine.ReleaseChannel("owner", 1);
        Assert.Same(pool, engine.GetPoolBySession("owner"));
        Assert.Contains("owner", pool!.Members);
        Assert.Equal(new[] { "other-listener" }, pool.ListenSessionIds);
        Assert.Null(engine.GetFullClaimer(1));
        engine.ClaimChannel("next", 1, ClaimLevel.Full);
        Assert.Equal("next", engine.ResolveActiveSession("owner"));
    }

    [Fact]
    public void ReleasingHistoricalListenerPreservesTakeoverAddressing()
    {
        var engine = TestHost.BuildSessionEngineWithFakeSession("old", true);
        engine.ClaimChannel("old", 1, ClaimLevel.Listen);
        var pool = engine.GetPoolBySession("old");
        engine.TakeOverSessionPool("old", "new");
        engine.ReleaseChannel("old", 1);
        Assert.Same(pool, engine.GetPoolBySession("old"));
        Assert.Equal("new", engine.ResolveActiveSession("old"));
        Assert.Equal("new", engine.GetFullClaimer(1));
        Assert.Empty(pool!.ListenSessionIds);
    }

    [Fact]
    public void ReleasingListenerInOldChannelDoesNotEraseNewChannelIndex()
    {
        var engine = TestHost.BuildSessionEngineWithFakeSession("owner", true);
        engine.ClaimChannel("listener", 1, ClaimLevel.Listen);
        engine.ClaimChannel("listener", 2, ClaimLevel.Full);
        var current = engine.GetPoolBySession("listener");
        engine.ReleaseChannel("listener", 1);
        Assert.Same(current, engine.GetPoolBySession("listener"));
        Assert.Equal(2, current!.ChannelId);
        Assert.Equal("listener", engine.GetFullClaimer(2));
        Assert.Empty(engine.GetPoolBySession("owner")!.ListenSessionIds);
    }
}
