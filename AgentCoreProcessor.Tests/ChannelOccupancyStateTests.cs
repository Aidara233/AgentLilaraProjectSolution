using System.Reflection;
using AgentCoreProcessor.Engine;
using AgentCoreProcessor.Engine.Session;
using Xunit;

namespace AgentCoreProcessor.Tests;

public sealed class ChannelOccupancyStateTests
{
    [Fact]
    public void RootPoolDelegatesToOneOccupancyObjectAcrossTakeovers()
    {
        var pool = new SessionPool(42);
        var field = typeof(SessionPool).GetField("_occupancy", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var occupancy = Assert.IsType<ChannelOccupancyState>(field.GetValue(pool));
        Assert.Equal(occupancy.ListenSessionIds, pool.ListenSessionIds);
        Assert.True(pool.Claim("old"));
        Assert.Equal("old", occupancy.ActiveSessionId);
        Assert.True(pool.Claim("old"));
        Assert.False(pool.Claim("conflict"));
        pool.Listen("listener");
        pool.TakeOver("new");
        Assert.Same(occupancy, field.GetValue(pool));
        Assert.Equal("new", occupancy.ActiveSessionId);
        Assert.Equal("new", pool.ActiveSessionId);
        Assert.True(pool.HasFullClaim);
        Assert.False(pool.Release("old"));
        Assert.True(pool.Release("new"));
        Assert.False(occupancy.HasFullClaim);
        Assert.Equal("", pool.ActiveSessionId);
        Assert.Equal(new[] { "listener" }, occupancy.ListenSessionIds);
        Assert.False(pool.IsEmpty);
        pool.Unlisten("listener");
        Assert.True(pool.IsEmpty);
        Assert.True(pool.Members.SetEquals(new[] { "old", "new" }));
        Assert.DoesNotContain("conflict", pool.Members);
    }

    [Fact]
    public void ChildPoolHasNoChannelOccupancyAndRetainsIdentitySemantics()
    {
        var pool = new SessionPool(0);
        Assert.Null(typeof(SessionPool).GetField("_occupancy", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pool));
        Assert.True(pool.Claim("first"));
        Assert.False(pool.HasFullClaim);
        // Existing child Claim accepts replacement because it does not own a Full claim.
        Assert.True(pool.Claim("second"));
        Assert.Equal("second", pool.ActiveSessionId);
        pool.TakeOver("third");
        Assert.False(pool.Release("second"));
        Assert.True(pool.Release("third"));
        Assert.Equal("", pool.ActiveSessionId);
        Assert.True(pool.Members.SetEquals(new[] { "first", "second", "third" }));
        Assert.False(pool.HasFullClaim);
    }

    [Theory]
    [InlineData(42)]
    [InlineData(0)]
    public void ListenerMutationIsIdempotentAndSnapshotsAreDetachedAndReadOnly(int channelId)
    {
        var pool = new SessionPool(channelId);
        Assert.True(pool.Listen("a"));
        Assert.False(pool.Listen("a"));
        var before = pool.ListenSessionIds;
        Assert.Equal(new[] { "a" }, before);
        var collection = Assert.IsAssignableFrom<IList<string>>(before);
        Assert.True(collection.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => collection.Add("injected"));
        Assert.Throws<NotSupportedException>(() => collection[0] = "changed");
        Assert.Throws<NotSupportedException>(() => collection.Clear());
        Assert.True(pool.Listen("b"));
        Assert.Equal(new[] { "a" }, before);
        Assert.Equal(new[] { "a", "b" }, pool.ListenSessionIds);
        Assert.True(pool.Unlisten("a"));
        Assert.False(pool.Unlisten("a"));
        Assert.Equal(new[] { "b" }, pool.ListenSessionIds);
        Assert.False(pool.IsEmpty);
        Assert.True(pool.Unlisten("b"));
        Assert.True(pool.IsEmpty);
        Assert.Empty(pool.Members);
        Assert.False(pool.HasFullClaim);
    }

    [Fact]
    public void ChannelStateOwnsListenerMutationAndPreservesFullClaim()
    {
        var occupancy = new ChannelOccupancyState();
        Assert.True(occupancy.Claim("owner"));
        Assert.True(occupancy.Listen("listener"));
        Assert.False(occupancy.Listen("listener"));
        var before = occupancy.ListenSessionIds;
        Assert.True(occupancy.HasListeners);
        Assert.True(occupancy.Unlisten("listener"));
        Assert.False(occupancy.Unlisten("listener"));
        Assert.False(occupancy.HasListeners);
        Assert.Equal(new[] { "listener" }, before);
        Assert.Equal("owner", occupancy.ActiveSessionId);
        Assert.True(occupancy.HasFullClaim);
        Assert.Empty(occupancy.ListenSessionIds);
    }

    [Fact]
    public void DifferentChannelsHaveIndependentOccupancy()
    {
        var first = new SessionPool(1);
        var second = new SessionPool(2);
        first.Claim("a");
        second.Claim("b");
        first.Listen("listener");
        Assert.Equal("b", second.ActiveSessionId);
        Assert.Empty(second.ListenSessionIds);
        first.TakeOver("next");
        Assert.Equal("b", second.ActiveSessionId);
    }
}
