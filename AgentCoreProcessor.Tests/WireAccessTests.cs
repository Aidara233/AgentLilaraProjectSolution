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
    [InlineData("Mounts")]
    [InlineData("SUBSCRIPTIONS")]
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

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"version\":1,\"mount\":[]}")]
    [InlineData("{\"mounts\":[]}")]
    public void MissingMountFieldsPreserveCurrentTable(string input)
    {
        Access.Apply(Source);
        const string mounts = """{"version":1,"mounts":[{"scope":"global","classId":"user.quiet"}]}""";
        Access.Mount(mounts);
        var path = Path.Combine(_root, "Graphs", "mounts.json");
        var before = File.ReadAllText(path);
        Assert.Throws<ArgumentException>(() => Access.Mount(input));
        Assert.Equal(before, File.ReadAllText(path));
    }

    [Fact]
    public void NormalizedChannelKeyAndLiveManagerReloadWork()
    {
        Access.Apply(Source);
        Access.Mount("""{"version":1,"mounts":[{"scope":"channel","scopeKey":"+042","classId":"user.quiet"}]}""");
        var manager = GraphManager.Open(_root, false);
        var first = Assert.Single(manager.RunnersFor(42));
        Access.Apply(Source.Replace("\"links\":[]", "\"variables\":[{\"name\":\"x\",\"type\":\"int\",\"initial\":9}],\"links\":[]"));
        Access.Mount("""{"version":1,"mounts":[{"scope":"channel","scopeKey":"42","classId":"user.quiet"}]}""");
        var path = Path.Combine(_root, "Graphs", "mounts.json");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(2));
        var second = Assert.Single(manager.RunnersFor(42));
        Assert.NotSame(first, second);
        Assert.Single(second.Instance.Class.Variables);
        Access.Mount("""{"version":1,"mounts":[]}""");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(4));
        Assert.Empty(manager.RunnersFor(42));
    }

    [Fact]
    public void ManualMountsNormalizeChannelAndRejectWrongActivation()
    {
        Access.Apply(Source);
        var path = Path.Combine(_root, "Graphs", "mounts.json");
        File.WriteAllText(path, """{"version":1,"mounts":[{"scope":"channel","scopeKey":"042","classId":"user.quiet"}]}""");
        Assert.Single(GraphManager.Open(_root, false).RunnersFor(42));
        File.WriteAllText(path, """{"version":1,"mounts":[{"scope":"global","classId":"user.quiet","trigger":"error"}]}""");
        Assert.Empty(GraphManager.Open(_root, false).RunnersForError(42));
    }

    [Fact]
    public void IncompatibleClassReplacementAndSessionMountAreRejected()
    {
        Access.Apply(Source);
        Access.Mount("""{"version":1,"mounts":[{"scope":"global","classId":"user.quiet"}]}""");
        Assert.Throws<ArgumentException>(() => Access.Apply(Source.Replace("on.message", "on.error")));
        Assert.Equal(Source, File.ReadAllText(Path.Combine(_root, "Graphs", "user.quiet.json")));
        Assert.Throws<ArgumentException>(() => Access.Mount("""{"version":1,"mounts":[{"scope":"session","scopeKey":"s1","classId":"user.quiet"}]}"""));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
}
