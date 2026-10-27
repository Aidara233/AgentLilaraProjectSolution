using System.Text.Json;
using AgentLilara.GraphRuntime.Contracts;
using AgentLilara.GraphRuntime.Graph;
using AgentLilara.GraphRuntime.Modules;
using AgentLilara.GraphRuntime.Serialization;
using Xunit;

namespace AgentLilara.GraphRuntime.Tests;

public sealed class ModuleInstanceTests
{
    private static ModuleHost Host() => new(ModuleCatalog.FromTypes(new[] { typeof(IDemoEcho) }));
    private static EvalContext Context() => new("instances", 1,
        InboundEvent.Message("test", "42", "hello", ChannelKind.Group));

    private static string Source(string? instance, bool write) => JsonSerializer.Serialize(new
    {
        classId = "test.instances", kind = "static",
        nodes = new object[]
        {
            new { id = "on", type = "on.message" },
            new { id = "value", type = "read.const", @params = new { value = "changed" } },
            new { id = "member", type = write ? "write.demo.echo.nickname" : "read.demo.echo.nickname",
                @params = instance is null ? new Dictionary<string, string>() : new() { ["instance"] = instance } },
            new { id = "send", type = "sink.send" }
        },
        links = write ? new[]
        {
            new { from = "on.event", to = "member.event" }, new { from = "value.value", to = "member.value" },
            new { from = "on.event", to = "send.event" }, new { from = "value.value", to = "send.text" }
        } : new[]
        {
            new { from = "on.event", to = "send.event" }, new { from = "member.value", to = "send.text" }
        }
    });

    private static string? Read(ModuleHost host, string? instance)
    {
        var graph = new GraphInstance(GraphLoader.Load(Source(instance, false), host.Registry));
        Assert.True(graph.Run(Context()).Success);
        return Assert.Single(graph.LastIntents).Args["text"].As<string>();
    }

    [Fact]
    public void NamedReadWriteBindingsAreIsolatedAndDefaultRemainsCompatible()
    {
        var first = new DemoEcho { Nickname = "first" };
        var second = new DemoEcho { Nickname = "second" };
        var fallback = new DemoEcho { Nickname = "default" };
        var host = Host().BindInstance("demo.echo", "a", first)
            .BindInstance("demo.echo", "b", second).Bind("demo.echo", fallback);
        Assert.Equal("first", Read(host, "a"));
        Assert.Equal("second", Read(host, "b"));
        Assert.Equal("default", Read(host, null));
        var writer = new GraphInstance(GraphLoader.Load(Source("a", true), host.Registry));
        Assert.True(writer.Run(Context()).Success);
        Assert.All(writer.LastSinkOutcomes, outcome => Assert.True(outcome.Success));
        Assert.Equal("changed", Read(host, "a"));
        Assert.Equal("second", Read(host, "b"));
        Assert.Equal("default", Read(host, null));
        Assert.Same(fallback, host.Implementations["demo.echo"]);
    }

    [Fact]
    public void UnknownInstanceDoesNotFallBackToDefaultAndDefaultRequiresOwnBinding()
    {
        var host = Host().Bind("demo.echo", new DemoEcho());
        Assert.Throws<GraphLoadException>(() => GraphLoader.Load(Source("missing", false), host.Registry));
        Assert.Throws<GraphLoadException>(() => GraphLoader.Load(Source("A", false),
            Host().BindInstance("demo.echo", "a", new DemoEcho()).Registry));
        Assert.Throws<GraphLoadException>(() => GraphLoader.Load(Source(null, false),
            Host().BindInstance("demo.echo", "a", new DemoEcho()).Registry));
    }

    [Fact]
    public void DuplicateInvalidAndUnknownBindingsAreRejected()
    {
        var host = Host().BindInstance("demo.echo", "a", new DemoEcho());
        Assert.Throws<ModuleDescriptorException>(() => host.BindInstance("demo.echo", "a", new DemoEcho()));
        Assert.Throws<ModuleDescriptorException>(() => host.BindInstance("missing", "a", new DemoEcho()));
        Assert.Throws<ModuleDescriptorException>(() => host.BindInstance("demo.echo", "b", new object()));
        Assert.Throws<ArgumentException>(() => host.BindInstance("demo.echo", " ", new DemoEcho()));
        Assert.Throws<ArgumentNullException>(() => host.BindInstance("demo.echo", "b", null!));
        Assert.Throws<GraphLoadException>(() => GraphLoader.Load(Source(" ", false), host.Registry));
    }
}
