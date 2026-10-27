using AgentCoreProcessor.Engine.Session;
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;
using Xunit;

namespace AgentCoreProcessor.Tests;

public sealed class EscalateReservationTests
{
    [Theory]
    [InlineData("missing")]
    [InlineData("listen")]
    [InlineData("other")]
    public async Task FailedReservationDoesNotCreateWorkingOrChangePool(string state)
    {
        var engine = TestHost.BuildSessionEngineWithFakeSession("existing", true, registerInPool: false);
        var express = new RuntimeSession(new CreateSessionSpec { Type = "express" }, "express", new[] { "basic-tools" });
        express.ConnectedChannel.Set(42, ConnectionMode.Full);
        if (state == "listen") engine.ClaimChannel("listener", 42, ClaimLevel.Listen);
        if (state == "other") engine.ClaimChannel("other", 42, ClaimLevel.Full);
        var sessions = engine.GetActiveSessions().Select(s => s.SessionId).ToArray();
        var pools = engine.GetPoolsSnapshot().ToArray();
        var members = pools.Select(p => p.Members.ToArray()).ToArray();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.CreateFromEscalateAsync(express, "reason"));
        Assert.Contains("无法接管频道 42", error.Message);
        Assert.Equal(sessions, engine.GetActiveSessions().Select(s => s.SessionId));
        Assert.Equal(pools, engine.GetPoolsSnapshot());
        for (var i = 0; i < pools.Length; i++) Assert.Equal(members[i], pools[i].Members);
        Assert.Equal(state == "other" ? "other" : null, engine.GetFullClaimer(42));
        Assert.Null(engine.GetPoolBySession("express"));
    }

    [Fact]
    public void ExpectedHolderIsCheckedUnderTakeoverLock()
    {
        var engine = TestHost.BuildSessionEngineWithFakeSession("express", true);
        Assert.True(engine.TakeOverChannelPool(1, "winner", "express"));
        Assert.False(engine.TakeOverChannelPool(1, "loser", "express"));
        Assert.Equal("winner", engine.GetFullClaimer(1));
        Assert.Null(engine.GetPoolBySession("loser"));
        Assert.True(engine.TakeOverChannelPool(1, "next", "winner"));
        Assert.Equal("next", engine.ResolveActiveSession("express"));
    }
}
