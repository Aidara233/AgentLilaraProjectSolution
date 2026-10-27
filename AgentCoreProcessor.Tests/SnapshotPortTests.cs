using AgentCoreProcessor.Engine.Session;
using AgentLilara.PluginSDK;
using Xunit;

namespace AgentCoreProcessor.Tests;

public sealed class SnapshotPortTests
{
    [Fact]
    public void DuplicateFieldReportsBothOwners()
    {
        var error = Assert.Throws<InvalidOperationException>(() => new SnapshotWriter().Capture(new ISnapshotPort[]
        {
            new SnapshotPort("first", w => w.Set(s => s.RoundCount, 1)),
            new SnapshotPort("second", w => w.Set(s => s.RoundCount, 2))
        }));
        Assert.Contains("RoundCount", error.Message);
        Assert.Contains("first", error.Message);
        Assert.Contains("second", error.Message);
    }

    [Fact]
    public void MissingFieldsFailInsteadOfSilentlySavingDefaults()
    {
        var error = Assert.Throws<InvalidOperationException>(() => new SnapshotWriter().Capture(Array.Empty<ISnapshotPort>()));
        Assert.Contains("SessionId", error.Message);
        Assert.Contains("Axes", error.Message);
    }

    [Fact]
    public void RestorePortsPreserveExistingRuntimeStateSemantics()
    {
        var session = new RuntimeSession(new CreateSessionSpec { Type = "channel" }, new[] { "basic-tools" });
        var created = DateTime.UtcNow.AddHours(-2);
        var wait = DateTime.UtcNow.AddMinutes(3);
        session.RestoreFromSnapshot(new SessionStateSnapshot
        {
            Status = SessionStatus.Crashed, CreatedAt = created, RoundCount = 23,
            WaitUntil = wait, LastActivity = created.AddMinutes(1), EffectiveParentId = "parent",
            ConnectedChannelId = "42", ConnectedMode = "Full",
            Axes = new() { ["initiative"] = "task" }
        }, new());
        var snapshot = session.BuildStateSnapshot();
        Assert.Equal(SessionStatus.Restored, snapshot.Status);
        Assert.Equal(created, snapshot.CreatedAt);
        Assert.Equal(23, snapshot.RoundCount);
        Assert.Equal(wait, snapshot.WaitUntil);
        Assert.Equal(created.AddMinutes(1), snapshot.LastActivity);
        Assert.Equal("parent", snapshot.EffectiveParentId);
        Assert.Equal("42", snapshot.ConnectedChannelId);
        Assert.Equal("Full", snapshot.ConnectedMode);
        Assert.Equal("task", snapshot.Axes["initiative"]);
    }

    [Fact]
    public void RuntimeRegistryCapturesAllFieldsAndCopiesMutableCollections()
    {
        var session = new RuntimeSession(new CreateSessionSpec { Type = "channel" }, new[] { "basic-tools" });
        session.InheritRoundCount(17);
        var snapshot = session.BuildStateSnapshot();
        Assert.Equal(session.SessionId, snapshot.SessionId);
        Assert.Equal(17, snapshot.RoundCount);
        Assert.Equal("None", snapshot.CompressionTier);
        Assert.Equal(7, snapshot.Axes.Count);
        snapshot.Axes.Clear();
        snapshot.ChannelCursors[99] = 100;
        var next = session.BuildStateSnapshot();
        Assert.Equal(7, next.Axes.Count);
        Assert.Empty(next.ChannelCursors);
    }
}
