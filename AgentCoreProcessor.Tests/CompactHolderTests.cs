using AgentCoreProcessor.Engine.Session;
using AgentLilara.PluginSDK;
using Xunit;

namespace AgentCoreProcessor.Tests;

public sealed class CompactHolderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExpectedHolderRejectsHistoryWithoutChangingIndexes(bool child)
    {
        var engine = TestHost.BuildSessionEngineWithFakeSession("root", true);
        var old = "root";
        if (child) { engine.CreateSubPool("child", "root"); old = "child"; }
        var pool = engine.GetPoolBySession(old)!;
        Assert.True(engine.TakeOverSessionPool(old, "current", old));
        var members = pool.Members.ToArray();
        Assert.False(engine.TakeOverSessionPool(old, "rejected", old));
        Assert.Equal("current", pool.ActiveSessionId);
        Assert.Equal(members, pool.Members);
        Assert.Null(engine.GetPoolBySession("rejected"));
        Assert.Same(pool, engine.GetPoolBySession(old));
        Assert.Same(pool, engine.GetPoolBySession("current"));
        Assert.True(engine.TakeOverSessionPool("current", "next", "current"));
        Assert.Equal("next", engine.ResolveActiveSession(old));
        // Historical addressing remains available to callers that omit the guard.
        Assert.True(engine.TakeOverSessionPool(old, "compatible"));
    }

    [Fact]
    public async Task ProductionCompactRejectsHistoricalCallerBeforeCreatingSession()
    {
        var engine = TestHost.BuildSessionEngineWithFakeSession("old", true);
        var old = new RuntimeSession(new CreateSessionSpec { Type = "channel" }, "old", new[] { "basic-tools" });
        engine.TakeOverSessionPool("old", "current");
        var pool = engine.GetPoolBySession("old")!;
        var members = pool.Members.ToArray();
        var sessions = engine.GetActiveSessions().Select(s => s.SessionId).ToArray();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.CreateFromCompact(old, "summary"));
        Assert.Contains("无法 compact", error.Message);
        Assert.Equal(sessions, engine.GetActiveSessions().Select(s => s.SessionId));
        Assert.Equal(members, pool.Members);
        Assert.Equal("current", engine.GetFullClaimer(1));
        Assert.Same(pool, engine.GetPoolBySession("current"));
    }
}
