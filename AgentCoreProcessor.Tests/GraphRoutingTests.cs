using AgentLilara.GraphRuntime.Contracts;
using AgentLilara.GraphRuntime.Graph;
using AgentLilara.GraphRuntime.Serialization;
using Xunit;

namespace AgentCoreProcessor.Tests;

public class GraphRoutingTests
{
    [Fact]
    public async Task RouterPresetEmitsConfiguredSessionType()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Graphs", "preset.router.json");
        Assert.True(File.Exists(path), $"Missing copied preset: {path}");
        var graph = new GraphInstance(GraphLoader.Load(File.ReadAllText(path)));
        var runner = AgentCoreProcessor.Engine.Graph.KeywordGraphRunner.ForInstance(graph, active: true);
        var dispatched = new List<ActionIntent>();

        var allow = await runner.DecideAsync(
            InboundEvent.Message("test", "42", "hello", ChannelKind.Group),
            "router-test",
            intent =>
            {
                dispatched.Add(intent);
                return Task.CompletedTask;
            });

        Assert.True(allow);
        var route = Assert.Single(dispatched);
        Assert.Equal(IntentKind.SessionRoute, route.Kind);
        Assert.Equal("channel", route.Target);
        Assert.Equal("channel", route.Args["sessionType"].As<string>());
    }

    [Fact]
    public void IntentCatalogExposesRouteSink()
    {
        var spec = Assert.Single(IntentCatalog.All, x => x.NodeType == "sink.route");
        Assert.Equal(IntentKind.SessionRoute, spec.Kind);
        Assert.Equal(new[] { "sessionType" }, spec.ArgPorts);
    }
}
