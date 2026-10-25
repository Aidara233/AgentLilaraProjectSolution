using AgentLilara.Kernel.Contracts;
using AgentLilara.Kernel.Graph;
using AgentLilara.Kernel.Modules;
using AgentLilara.Kernel.Serialization;
using Xunit;

namespace AgentLilara.Kernel.Tests;

// ── 测试用模块：一个"能记日志的回声" ─────────────────────────────

[Module("demo.echo", Version = "0.2.0", RequiredCapabilities = new[] { Capabilities.Disk })]
public interface IDemoEcho
{
    [Lifecycle("mount")]
    Task OnMountAsync();

    [Method(SideEffect = true)]
    int Echo(string text, int times = 2);

    [Method]
    bool Check(int value, int threshold);

    [Property]
    string? Nickname { get; set; }

    [Property(ReadOnly = true)]
    int Calls { get; }
}

public sealed class DemoEcho : IDemoEcho
{
    public List<string> Log { get; } = new();

    public int Calls { get; private set; }

    public string? Nickname { get; set; }

    public bool Mounted { get; private set; }

    public Task OnMountAsync()
    {
        Mounted = true;
        return Task.CompletedTask;
    }

    public int Echo(string text, int times)
    {
        Calls++;
        Log.Add($"{text}x{times}");
        return text.Length * times;
    }

    public bool Check(int value, int threshold) => value >= threshold;
}

// 反例：未标注 [Method]，生成期就该失败
[Module("demo.bad.unannotated")]
public interface IUnannotatedMethod
{
    void DoSomething();
}

// 反例：参数类型承载不了（delegate）
[Module("demo.bad.delegate")]
public interface IDelegateParam
{
    [Method]
    void Run(Func<int> factory);
}

public sealed class ModuleDescriptorTests
{
    private static ModuleDescriptor EchoModule() => ModuleDescriptorGenerator.Generate(typeof(IDemoEcho));

    [Fact]
    public void GeneratesMethodsParamsAndLifecycle()
    {
        var module = EchoModule();

        Assert.Equal("demo.echo", module.Id);
        Assert.Equal("0.2.0", module.Version);
        Assert.Equal(new[] { Capabilities.Disk }, module.RequiredCapabilities);
        Assert.Equal(new[] { "mount" }, module.Activations.Select(a => a.Phase));

        var echo = module.FindMethod("echo")!;
        Assert.Equal("int", echo.ReturnType);
        Assert.Equal(new[] { "result" }, echo.Returns);
        Assert.True(echo.SideEffect);
        Assert.Equal(30_000, echo.TimeoutMs);

        Assert.Equal(new[] { "text", "times" }, echo.Params.Select(p => p.Name));
        Assert.False(echo.Params[0].Optional);
        Assert.True(echo.Params[1].Optional);
        Assert.Equal(2, echo.Params[1].Default.AsInt());

        Assert.Null(module.FindMethod("onMountAsync")); // 生命周期方法不进方法节点
    }

    [Fact]
    public void GeneratesPropertiesWithReadOnlyFlag()
    {
        var module = EchoModule();

        Assert.False(module.FindProperty("nickname")!.ReadOnly);
        Assert.True(module.FindProperty("calls")!.ReadOnly);
    }

    [Fact]
    public void NodeTypesFollowNamingRules()
    {
        var catalog = ModuleCatalog.FromTypes(new[] { typeof(IDemoEcho) });

        Assert.Equal(
            new[]
            {
                "module.demo.echo.check",
                "module.demo.echo.echo",
                "read.demo.echo.calls",
                "read.demo.echo.nickname",
                "write.demo.echo.nickname",
            },
            catalog.NodeTypes);
    }

    [Fact]
    public void CatalogJsonListsNodes()
    {
        var json = ModuleCatalog.FromTypes(new[] { typeof(IDemoEcho) }).ToJson();

        Assert.Contains("\"module.demo.echo.echo\"", json);
        Assert.Contains("\"write.demo.echo.nickname\"", json);
        Assert.DoesNotContain("\"write.demo.echo.calls\"", json);
    }

    [Fact]
    public void RejectsUnannotatedMethod()
    {
        var ex = Assert.Throws<ModuleDescriptorException>(
            () => ModuleDescriptorGenerator.Generate(typeof(IUnannotatedMethod)));
        Assert.Contains("DoSomething", ex.Message);
    }

    [Fact]
    public void RejectsUnsupportedParameterType()
    {
        var ex = Assert.Throws<ModuleDescriptorException>(
            () => ModuleDescriptorGenerator.Generate(typeof(IDelegateParam)));
        Assert.Contains("unsupported type", ex.Message);
    }

    [Fact]
    public void RejectsNonInterface()
    {
        Assert.Throws<ModuleDescriptorException>(
            () => ModuleDescriptorGenerator.Generate(typeof(DemoEcho)));
    }
}

public sealed class ModuleGraphTests
{
    private const string EchoGraph = """
    {
      "classId": "demo.caller",
      "kind": "static",
      "version": 1,
      "nodes": [
        { "id": "on",   "type": "on.message" },
        { "id": "text", "type": "read.const", "params": { "value": "hi" } },
        { "id": "call", "type": "module.demo.echo.echo" }
      ],
      "links": [
        { "from": "on.event",   "to": "call.event" },
        { "from": "text.value", "to": "call.text" }
      ]
    }
    """;

    private static (ModuleHost Host, DemoEcho Implementation) NewHost()
    {
        var host = new ModuleHost(ModuleCatalog.FromTypes(new[] { typeof(IDemoEcho) }));
        var implementation = new DemoEcho();
        host.Bind("demo.echo", implementation);
        return (host, implementation);
    }

    private static EvalContext Context(long seq = 1) =>
        new($"trace-{seq}", seq, InboundEvent.Message("file", "chan-1", "hello"));

    [Fact]
    public void MethodNodeCallsImplementation_WithOptionalDefault()
    {
        var (host, implementation) = NewHost();
        var graph = GraphLoader.Load(EchoGraph, host.Registry);
        var instance = new GraphInstance(graph);

        var result = instance.Run(Context());

        Assert.True(result.Success);
        Assert.Equal(new[] { "hix2" }, implementation.Log); // times 未接线 → 用声明默认值 2
        Assert.Equal(1, implementation.Calls);

        var outcome = Assert.Single(instance.LastSinkOutcomes);
        Assert.True(outcome.Success);
        Assert.Equal(4, outcome.Outputs["result"].AsInt());
    }

    [Fact]
    public void MethodNodeWaitsForEvent()
    {
        // 事件被 gate.if 挡掉（cond = false）→ 本拍不调用
        const string gated = """
        {
          "classId": "demo.gated",
          "kind": "static",
          "version": 1,
          "nodes": [
            { "id": "on",   "type": "on.message" },
            { "id": "text", "type": "read.const", "params": { "value": "hi" } },
            { "id": "no",   "type": "read.const", "params": { "value": false } },
            { "id": "gate", "type": "gate.if" },
            { "id": "call", "type": "module.demo.echo.echo" }
          ],
          "links": [
            { "from": "on.event",   "to": "gate.event" },
            { "from": "no.value",   "to": "gate.cond" },
            { "from": "gate.event", "to": "call.event" },
            { "from": "text.value", "to": "call.text" }
          ]
        }
        """;

        var (host, implementation) = NewHost();
        var instance = new GraphInstance(GraphLoader.Load(gated, host.Registry));

        Assert.True(instance.Run(Context()).Success);
        Assert.Empty(implementation.Log);
        Assert.Empty(instance.LastSinkOutcomes);
    }

    [Fact]
    public void PropertyWriteCommitsThenIsReadable()
    {
        const string writer = """
        {
          "classId": "demo.nick",
          "kind": "static",
          "version": 1,
          "nodes": [
            { "id": "on",    "type": "on.message" },
            { "id": "name",  "type": "read.const", "params": { "value": "阿离" } },
            { "id": "write", "type": "write.demo.echo.nickname" },
            { "id": "read",  "type": "read.demo.echo.nickname" },
            { "id": "send",  "type": "sink.send" }
          ],
          "links": [
            { "from": "on.event",   "to": "write.event" },
            { "from": "name.value", "to": "write.value" },
            { "from": "read.value", "to": "send.text" },
            { "from": "on.event",   "to": "send.event" }
          ]
        }
        """;

        var (host, implementation) = NewHost();
        var instance = new GraphInstance(GraphLoader.Load(writer, host.Registry));

        Assert.True(instance.Run(Context()).Success);
        Assert.Equal("阿离", implementation.Nickname);
    }

    [Fact]
    public void UnboundModuleFailsToLoad()
    {
        var host = new ModuleHost(ModuleCatalog.FromTypes(new[] { typeof(IDemoEcho) }));

        var ex = Assert.Throws<GraphLoadException>(() => GraphLoader.Load(EchoGraph, host.Registry));
        Assert.Contains("no bound implementation", ex.Message);
    }

    [Fact]
    public void ReadOnlyPropertyHasNoWriteNode()
    {
        var (host, _) = NewHost();
        var graph = """
        {
          "classId": "demo.badwrite",
          "kind": "static",
          "version": 1,
          "nodes": [
            { "id": "on",   "type": "on.message" },
            { "id": "one",  "type": "read.const", "params": { "value": 1 } },
            { "id": "call", "type": "write.demo.echo.calls" }
          ],
          "links": [
            { "from": "on.event",  "to": "call.event" },
            { "from": "one.value", "to": "call.value" }
          ]
        }
        """;

        var ex = Assert.Throws<GraphLoadException>(() => GraphLoader.Load(graph, host.Registry));
        Assert.Contains("unknown type", ex.Message);
    }

    [Fact]
    public void BindingRequiresMatchingInterface()
    {
        var host = new ModuleHost(ModuleCatalog.FromTypes(new[] { typeof(IDemoEcho) }));

        Assert.Throws<ModuleDescriptorException>(() => host.Bind("demo.echo", new object()));
    }
}
