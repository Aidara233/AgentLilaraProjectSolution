using System.Text.Json;
using AgentCoreProcessor.Engine.Graph;
using AgentCoreProcessor.Tool.Host;
using Xunit;

namespace AgentCoreProcessor.Tests;

public sealed class WireAccessTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wire-tests-" + Guid.NewGuid().ToString("N"));
    private WireAccessImpl Access => new(_root, Path.Combine(_root, "defaults"));
    private const string Source = """
        {"classId":"user.quiet","kind":"static","nodes":[{"id":"in","type":"on.message"}],"links":[]}
        """;

    [Fact]
    public void ApplyMountAndLoadThroughRealManager()
    {
        var access = Access;
        access.Apply(Source);
        access.Mount("""{"version":1,"mounts":[{"scope":"channel","scopeKey":"42","classId":"user.quiet"}]}""");
        var manager = GraphManager.Open(_root, active: false);
        Assert.Single(manager.RunnersFor(42));
        Assert.Empty(manager.RunnersFor(43));
        access.Mount("""{"version":1,"mounts":[]}""");
        Assert.Empty(GraphManager.Open(_root, false).RunnersFor(42));
        Assert.Equal(3, Directory.GetDirectories(Path.Combine(_root, "Graphs", ".history")).Length);
    }

    [Fact]
    public void InvalidReplacementPreservesSourceAndAuditStoresBothVersions()
    {
        var access = Access;
        access.Apply(Source);
        Assert.ThrowsAny<Exception>(() => access.Apply(Source.Replace("on.message", "missing.node")));
        Assert.Equal(Source, File.ReadAllText(Path.Combine(_root, "Graphs", "user.quiet.json")));
        var replacement = Source.Replace("\"links\":[]", "\"version\":2,\"links\":[]");
        access.Apply(replacement);
        var changes = Directory.GetFiles(Path.Combine(_root, "Graphs", ".history"), "change.json", SearchOption.AllDirectories)
            .Select(p => JsonDocument.Parse(File.ReadAllText(p))).ToArray();
        try
        {
            Assert.Contains(changes, d => d.RootElement.GetProperty("before").GetString() == Source
                && d.RootElement.GetProperty("after").GetString() == replacement);
        }
        finally { foreach (var change in changes) change.Dispose(); }
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("mounts")]
    [InlineData("subscriptions")]
    public void RejectsUnsafeOrReservedClassIds(string id)
    {
        Assert.Throws<ArgumentException>(() => Access.Apply(Source.Replace("user.quiet", id)));
        Assert.False(Directory.Exists(Path.Combine(_root, "Graphs")));
    }

    [Fact]
    public void WrongTriggerDoesNotWriteMounts()
    {
        Access.Apply(Source);
        Assert.Throws<ArgumentException>(() => Access.Mount("""{"version":1,"mounts":[{"scope":"global","classId":"user.quiet","trigger":"error"}]}"""));
        Assert.False(File.Exists(Path.Combine(_root, "Graphs", "mounts.json")));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
}
