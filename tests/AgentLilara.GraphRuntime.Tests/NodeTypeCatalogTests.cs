using System.Text.Json;
using AgentLilara.GraphRuntime.Graph;
using AgentLilara.GraphRuntime.Serialization;
using Xunit;

namespace AgentLilara.GraphRuntime.Tests;

public class NodeTypeCatalogTests
{
    [Fact]
    public void CatalogCoversEveryRegisteredTypeAndExamplesConstructMatchingNodes()
    {
        var factory = new BuiltInNodeFactory();
        var catalog = factory.NodeTypes;
        Assert.Equal(factory.Types.OrderBy(x => x), catalog.Select(x => x.Type).OrderBy(x => x));
        Assert.Equal(catalog.Count, catalog.Select(x => x.Type).Distinct().Count());
        foreach (var spec in catalog)
        {
            var parameters = spec.Params.ToDictionary(p => p.Name,
                p => JsonSerializer.Deserialize<JsonElement>(p.Example));
            var node = factory.Create("test", spec.Type, parameters);
            Assert.Equal(node.Kind, spec.Kind);
            Assert.Equal(node.IsActivationSource, spec.IsActivationSource);
            Assert.Equal(node.InputPorts, spec.InputPorts);
            Assert.Equal(node.OutputPorts, spec.OutputPorts);
            foreach (var required in spec.Params.Where(p => p.Required))
            {
                var missing = parameters.Where(p => p.Key != required.Name).ToDictionary();
                Assert.Throws<GraphLoadException>(() => factory.Create("test", spec.Type, missing));
            }
        }
    }

    [Fact]
    public void SampleLoadsThroughProductionLoader()
    {
        var graph = GraphLoader.Load(NodeTypeCatalog.SampleSource());
        Assert.Equal("user.example", graph.ClassId);
        Assert.Single(graph.Nodes, n => n.IsActivationSource);
    }
}
