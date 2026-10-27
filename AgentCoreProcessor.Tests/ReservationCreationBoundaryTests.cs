using System.Reflection;
using AgentCoreProcessor.Engine.Session;
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;
using Xunit;

namespace AgentCoreProcessor.Tests;

public sealed class ReservationCreationBoundaryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreparationFailureCancelsReservationBeforeExternalAccess(bool child)
    {
        var engine = TestHost.BuildSessionEngineWithFakeSession("root", true, failOnMindAccess: true);
        var old = "root";
        if (child) { engine.CreateSubPool("child", "root"); old = "child"; }
        var pool = engine.GetPoolBySession(old)!;
        var ticket = engine.BeginPoolReservation(old, "new")!;
        // A controlled invalid spec fails before construction or external service access.
        Assert.Throws<NullReferenceException>(() => engine.CreateSession(null!, "new", ticket));
        Assert.Equal(old, pool.ActiveSessionId);
        Assert.Null(engine.GetPoolBySession("new"));
        Assert.DoesNotContain("new", pool.Members);
        Assert.False(engine.TryCancelReservation(ticket));
        Assert.Single(engine.GetActiveSessions());
    }

    [Fact]
    public void MismatchedTicketIsRejectedWithoutConsumingItsReservation()
    {
        var engine = TestHost.BuildSessionEngineWithFakeSession("old", true);
        var other = TestHost.BuildSessionEngineWithFakeSession("other", true);
        var ticket = engine.BeginPoolReservation("old", "new")!;
        var spec = new CreateSessionSpec { Type = "channel", ResolvedComponentIds = Array.Empty<string>() };
        Assert.Throws<ArgumentException>(() => engine.CreateSession(spec, "wrong-id", ticket));
        Assert.Throws<ArgumentException>(() => other.CreateSession(spec, "new", ticket));
        Assert.Equal("new", engine.GetFullClaimer(1));
        Assert.True(engine.TryCancelReservation(ticket));
        Assert.Equal("old", engine.GetFullClaimer(1));
    }

    [Fact]
    public void ExternalServiceFailureAfterPublicationDoesNotCancelReservation()
    {
        var engine = TestHost.BuildSessionEngineWithFakeSession("old", true, failOnMindAccess: true);
        var ticket = engine.BeginPoolReservation("old", "new")!;
        var error = Assert.Throws<InvalidOperationException>(() => engine.CreateSession(
            new CreateSessionSpec { Type = "channel", ResolvedComponentIds = Array.Empty<string>() }, "new", ticket));
        Assert.Equal("controlled external service failure", error.Message);
        Assert.Equal("new", engine.GetFullClaimer(1));
        Assert.NotNull(engine.GetPoolBySession("new"));
        Assert.False(engine.TryCancelReservation(ticket));
        Assert.Single(engine.GetActiveSessions());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompactSpecFailureHappensBeforeReservation(bool hasPool)
    {
        var engine = TestHost.BuildSessionEngineWithFakeSession("old", true, registerInPool: hasPool);
        var old = new RuntimeSession(new CreateSessionSpec { Type = "channel" }, "old", new[] { "basic-tools" });
        typeof(RuntimeSession).GetField("_componentIds", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(old, null);
        var pool = engine.GetPoolBySession("old");
        await Assert.ThrowsAsync<NullReferenceException>(() => engine.CreateFromCompact(old, "summary"));
        if (hasPool)
        {
            Assert.Equal("old", pool!.ActiveSessionId);
            Assert.Equal(new[] { "old" }, pool.Members);
        }
        else Assert.Empty(engine.GetPoolsSnapshot());
        Assert.Single(engine.GetActiveSessions());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompactAndEscalateKeepPublishedReservationOnExternalFailure(bool escalate)
    {
        var engine = TestHost.BuildSessionEngineWithFakeSession("old", true, failOnMindAccess: true);
        var old = new RuntimeSession(new CreateSessionSpec { Type = escalate ? "express" : "channel" }, "old", new[] { "basic-tools" });
        old.ConnectedChannel.Set(1, ConnectionMode.Full);
        var pool = engine.GetPoolBySession("old")!;
        await Assert.ThrowsAsync<InvalidOperationException>(() => escalate
            ? engine.CreateFromEscalateAsync(old, "reason") : engine.CreateFromCompact(old, "summary"));
        Assert.NotEqual("old", pool.ActiveSessionId);
        Assert.Same(pool, engine.GetPoolBySession(pool.ActiveSessionId));
        Assert.Equal(2, pool.Members.Count);
        Assert.Single(engine.GetActiveSessions());
    }
}
