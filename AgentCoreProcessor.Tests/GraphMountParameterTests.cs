using AgentCoreProcessor.Engine.Graph;
using AgentLilara.GraphRuntime.Contracts;
using AgentLilara.GraphRuntime.Graph;
using Xunit;

namespace AgentCoreProcessor.Tests;

public class GraphMountParameterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lilara-mount-params-" + Guid.NewGuid().ToString("N"));
    private string GraphDir => Path.Combine(_root, "Graphs");

    public GraphMountParameterTests()
    {
        Directory.CreateDirectory(GraphDir);
    }

    [Fact]
    public async Task RouterUsesSessionTypeFromChannelMountParameters()
    {
        File.WriteAllText(Path.Combine(GraphDir, "mounts.json"), """
        {
          "version": 1,
          "mounts": [
            { "scope": "channel", "scopeKey": "42", "classId": "preset.router", "enabled": true,
              "params": { "sessionType": "task" } }
          ]
        }
        """);

        var manager = GraphManager.TryCreate(_root, active: true)!;
        var router = Assert.Single(manager.RunnersFor(42));
        var evaluated = await router.EvaluateAsync(
            InboundEvent.Message("test", "42", "do work", ChannelKind.Group),
            "mount-param-test");

        var route = Assert.Single(evaluated);
        Assert.Equal(IntentKind.SessionRoute, route.Kind);
        Assert.Equal("task", route.Target);
        Assert.Equal("task", route.Args["sessionType"].As<string>());
    }

    [Fact]
    public async Task MissingRouterParameterFailsClosedAtIntentSink()
    {
        File.WriteAllText(Path.Combine(GraphDir, "mounts.json"), """
        {
          "version": 1,
          "mounts": [
            { "scope": "channel", "scopeKey": "42", "classId": "preset.router", "enabled": true }
          ]
        }
        """);

        var manager = GraphManager.TryCreate(_root, active: true)!;
        var router = Assert.Single(manager.RunnersFor(42));

        var evaluated = await router.EvaluateAsync(
            InboundEvent.Message("test", "42", "do work", ChannelKind.Group),
            "missing-param-test");

        Assert.Empty(evaluated);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }
}
