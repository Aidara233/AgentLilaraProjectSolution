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
