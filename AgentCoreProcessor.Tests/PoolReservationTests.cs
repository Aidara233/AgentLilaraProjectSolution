using AgentCoreProcessor.Engine.Session;
using AgentLilara.PluginSDK;
using Xunit;

namespace AgentCoreProcessor.Tests;

public sealed class PoolReservationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellationRestoresIdentityAndOnlyRemovesAddedState(bool child)
    {
        var engine = TestHost.BuildSessionEngineWithFakeSession("root", true);
        var old = "root";
        if (child) { engine.CreateSubPool("child", "root"); old = "child"; }
        var pool = engine.GetPoolBySession(old)!;
        var ticket = engine.BeginPoolReservation(old, "new");
        Assert.NotNull(ticket);
        Assert.Equal("new", engine.ResolveActiveSession(old));
        Assert.True(engine.TryCancelReservation(ticket));
        Assert.Equal(old, pool.ActiveSessionId);
        Assert.Null(engine.GetPoolBySession("new"));
        Assert.DoesNotContain("new", pool.Members);
        Assert.False(engine.TryCancelReservation(ticket));
    }

    [Fact]
    public void ExistingHistoricalMemberAndIndexArePreserved()
    {
        var engine = TestHost.BuildSessionEngineWithFakeSession("old", true);
        engine.TakeOverSessionPool("old", "current");
        var pool = engine.GetPoolBySession("old");
        var ticket = engine.BeginPoolReservation("current", "old")!;
        Assert.True(engine.TryCancelReservation(ticket));
        Assert.Same(pool, engine.GetPoolBySession("old"));
        Assert.Contains("old", pool!.Members);
        Assert.Equal("current", engine.ResolveActiveSession("old"));
    }

    [Fact]
    public void AbaAndPublishedTicketsCannotCancel()
    {
        var engine = TestHost.BuildSessionEngineWithFakeSession("old", true);
        var ticket = engine.BeginPoolReservation("old", "new")!;
        engine.TakeOverSessionPool("new", "third");
        engine.TakeOverSessionPool("third", "new");
        Assert.False(engine.TryCancelReservation(ticket));
        var next = engine.BeginPoolReservation("new", "fourth")!;
        next.MarkPublished();
        Assert.False(engine.TryCancelReservation(next));
        Assert.Equal("fourth", engine.GetFullClaimer(1));
    }

    [Fact]
    public void ForeignEngineAndChangedIndexCannotCancel()
    {
        var engine = TestHost.BuildSessionEngineWithFakeSession("old", true);
        var other = TestHost.BuildSessionEngineWithFakeSession("other", true);
        var ticket = engine.BeginPoolReservation("old", "new")!;
        Assert.False(other.TryCancelReservation(ticket));
        engine.ClaimChannel("new", 2, ClaimLevel.Full);
        Assert.False(engine.TryCancelReservation(ticket));
        Assert.Equal("new", engine.GetFullClaimer(1));
        Assert.Equal(2, engine.GetPoolBySession("new")!.ChannelId);
    }

    [Fact]
    public void CancellationPreservesUnindexedHistoricalMember()
    {
        var engine = TestHost.BuildSessionEngineWithFakeSession("old", true);
        var pool = engine.GetPoolBySession("old")!;
        pool.Members.Add("historical");
        var ticket = engine.BeginPoolReservation("old", "historical")!;
        Assert.True(engine.TryCancelReservation(ticket));
        Assert.Contains("historical", pool.Members);
        Assert.Null(engine.GetPoolBySession("historical"));
        Assert.Equal("old", pool.ActiveSessionId);
    }

    [Fact]
    public void ReleaseAndClaimAbaInvalidatesTicketWhileIdempotentClaimDoesNot()
    {
        var engine = TestHost.BuildSessionEngineWithFakeSession("old", true);
        var ticket = engine.BeginPoolReservation("old", "new")!;
        Assert.True(engine.ClaimChannel("new", 1, ClaimLevel.Full));
        Assert.True(engine.TryCancelReservation(ticket));
        var next = engine.BeginPoolReservation("old", "next")!;
        var pool = engine.GetPoolBySession("old")!;
        Assert.True(pool.Release("next"));
        Assert.True(pool.Claim("next"));
        Assert.False(engine.TryCancelReservation(next));
        Assert.Equal("next", pool.ActiveSessionId);
    }

    [Fact]
    public void ConflictAndHistoricalCallerDoNotCreateTickets()
    {
        var engine = TestHost.BuildSessionEngineWithFakeSession("old", true);
        engine.ClaimChannel("other", 2, ClaimLevel.Listen);
        Assert.Null(engine.BeginPoolReservation("old", "other"));
        engine.TakeOverSessionPool("old", "current");
        Assert.Null(engine.BeginPoolReservation("old", "new"));
        Assert.Null(engine.BeginPoolReservation("missing", "new"));
        Assert.Equal("current", engine.GetFullClaimer(1));
    }
}
