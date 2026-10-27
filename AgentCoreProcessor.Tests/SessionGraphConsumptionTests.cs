using System.Reflection;
using System.Text.Json;
using AgentCoreProcessor.Database;
using AgentCoreProcessor.Engine.Graph;
using AgentCoreProcessor.Engine.Session;
using AgentCoreProcessor.Models;
using AgentCoreProcessor.Tool.Host;
using AgentLilara.PluginSDK;
using Xunit;

namespace AgentCoreProcessor.Tests;

public sealed class SessionGraphConsumptionTests
{
    private static string Source(string target) => """
        {"classId":"user.session","kind":"static","nodes":[
          {"id":"in","type":"on.message"},
          {"id":"target","type":"read.const","params":{"value":TARGET}},
          {"id":"text","type":"read.const","params":{"value":"graph note"}},
          {"id":"inject","type":"sink.inject"}],
         "links":[{"from":"in.event","to":"inject.event"},{"from":"target.value","to":"inject.sessionRef"},{"from":"text.value","to":"inject.text"}]}
        """.Replace("TARGET", JsonSerializer.Serialize(target));

    [Theory]
    [InlineData(true, true, 1)]
    [InlineData(false, true, 0)]
    [InlineData(true, false, 0)]
    public async Task ConsumesDynamicMountAndHonorsShadowAndLocalTarget(bool active, bool ownTarget, int expected)
    {
        var root = Path.Combine(Path.GetTempPath(), "session-graph-" + Guid.NewGuid().ToString("N"));
        try
        {
            var session = new RuntimeSession(new CreateSessionSpec { Type = "channel" }, new[] { "basic-tools" });
            var access = new WireAccessImpl(root);
            access.Apply(Source(ownTarget ? session.SessionId : "other-session"));
            var manager = GraphManager.Open(root, active);
            session.SetGraphRunnerProvider(() => manager.RunnersForSession(session.SessionId));
            access.Mount(JsonSerializer.Serialize(new { version = 1, mounts = new[] {
                new { scope = "session", scopeKey = session.SessionId, classId = "user.session" } } }));
            var message = new UserMessage { Content = "hello", Id = 1 };
            var batch = new[] { (42, message, false), (42, message, true) };
            await session.ConsumeSessionGraphsAsync(batch);
            var history = (List<Message>)typeof(RuntimeSession).GetField("_history", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;
            Assert.Equal(expected, history.Count);
            if (expected != 0) Assert.Equal("graph note", history[0].Content);
            Assert.Equal(1, manager.InstanceCount);
            access.Mount("""{"version":1,"mounts":[]}""");
            File.SetLastWriteTimeUtc(Path.Combine(root, "Graphs", "mounts.json"), DateTime.UtcNow.AddSeconds(2));
            await session.ConsumeSessionGraphsAsync(batch);
            Assert.Equal(expected, history.Count);
            Assert.Equal(0, manager.InstanceCount);
            manager.RunnersForSession(session.SessionId);
            manager.ReleaseSession(session.SessionId);
            Assert.Equal(0, manager.InstanceCount);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task TypeMountsMergeWithOwnMountsAndKeepInstancesIsolated()
    {
        var root = Path.Combine(Path.GetTempPath(), "type-graph-" + Guid.NewGuid().ToString("N"));
        try
        {
            var access = new WireAccessImpl(root);
            var source = Source("placeholder").Replace("\"type\":\"read.const\",\"params\":{\"value\":\"placeholder\"}",
                "\"type\":\"read.mount\",\"params\":{\"name\":\"sessionId\"}");
            source = source.Replace("\"kind\":\"static\"", "\"kind\":\"static\",\"variables\":[{\"name\":\"count\",\"type\":\"int\",\"initial\":0}]")
                .Replace("{\"id\":\"in\",\"type\":\"on.message\"}",
                    "{\"id\":\"in\",\"type\":\"on.message\"},{\"id\":\"one\",\"type\":\"read.const\",\"params\":{\"value\":1}},{\"id\":\"write\",\"type\":\"write.variable\",\"params\":{\"name\":\"count\"}}")
                .Replace("\"links\":[", "\"links\":[{\"from\":\"one.value\",\"to\":\"write.value\"},");
            access.Apply(source);
            access.Mount("""
                {"version":1,"mounts":[
                {"scope":"session","scopeKey":"a","classId":"user.session","instanceName":"own","order":5},
                {"scope":"session-type","scopeKey":"channel","classId":"user.session","instanceName":"type","order":5}]}
                """);
            var manager = GraphManager.Open(root, true);
            var a = manager.RunnersForSession("a", "channel");
            var b = manager.RunnersForSession("b", "CHANNEL");
            Assert.Equal(2, a.Count);
            Assert.Contains("#own@", a[0].Instance.InstanceKey);
            Assert.Single(b);
            Assert.NotSame(a[1], b[0]);
            Assert.Empty(manager.RunnersForSession("c", "task"));
            var session = new RuntimeSession(new CreateSessionSpec { Type = "channel" }, "a", new[] { "basic-tools" });
            session.SetGraphRunnerProvider(() => manager.RunnersForSession(session.SessionId, session.Type));
            await session.ConsumeSessionGraphsAsync(new[] { (42, new UserMessage { Id = 1, Content = "hello" }, false) });
            var history = (List<Message>)typeof(RuntimeSession).GetField("_history", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;
            Assert.Equal(2, history.Count);
            Assert.Equal(1, a[1].Instance.ReadVariable("count").AsInt());
            Assert.Equal(0, b[0].Instance.ReadVariable("count").AsInt());
            manager.ReleaseSession("a");
            Assert.Equal(1, manager.InstanceCount);
            Assert.Same(b[0], Assert.Single(manager.RunnersForSession("b", "channel")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ProviderFailureDoesNotAbortSessionInput()
    {
        var session = new RuntimeSession(new CreateSessionSpec { Type = "channel" }, new[] { "basic-tools" });
        session.SetGraphRunnerProvider(() => throw new InvalidOperationException("bad mount"));
        await session.ConsumeSessionGraphsAsync(new[] { (42, new UserMessage { Content = "hello" }, false) });
    }

    [Fact]
    public void RejectsUnsupportedActionsBeforeMounting()
    {
        var graph = AgentLilara.GraphRuntime.Serialization.GraphLoader.Load(
            Source("s").Replace("sink.inject", "sink.prompt"));
        Assert.Throws<ArgumentException>(() => SessionGraphContract.Validate(graph));
    }
}
