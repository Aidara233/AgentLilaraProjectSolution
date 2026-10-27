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
        Assert.Same(occupancy.ListenSessionIds, pool.ListenSessionIds);
        Assert.True(pool.Claim("old"));
        Assert.Equal("old", occupancy.ActiveSessionId);
        Assert.True(pool.Claim("old"));
        Assert.False(pool.Claim("conflict"));
        pool.ListenSessionIds.Add("listener");
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
        pool.ListenSessionIds.Clear();
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

    [Fact]
    public void DifferentChannelsHaveIndependentOccupancy()
    {
        var first = new SessionPool(1);
        var second = new SessionPool(2);
        first.Claim("a");
        second.Claim("b");
        first.ListenSessionIds.Add("listener");
        Assert.Equal("b", second.ActiveSessionId);
        Assert.Empty(second.ListenSessionIds);
        first.TakeOver("next");
        Assert.Equal("b", second.ActiveSessionId);
    }
}
