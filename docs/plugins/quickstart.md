# 快速上手：创建一个插件

本文从零开始创建一个简单的插件，涵盖 Global 和 Loop 两种组件模式。

## 1. 创建项目

在 `Plugins/` 下创建新的 Class Library 项目，引用 `AgentLilara.PluginSDK`。

**Plugin.MyFirst.csproj**：

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <RootNamespace>Plugin.MyFirst</RootNamespace>
  </PropertyGroup>

  <ItemGroup>
    <!-- Private=false 防止 SDK 程序集被复制到输出目录 -->
    <ProjectReference Include="..\..\AgentLilara.PluginSDK\AgentLilara.PluginSDK.csproj">
      <Private>false</Private>
    </ProjectReference>
  </ItemGroup>

  <!-- 编译后自动复制 DLL 到宿主 Plugins/ 目录 -->
  <Target Name="CopyToHostPlugins" AfterTargets="Build">
    <Copy SourceFiles="$(OutputPath)Plugin.MyFirst.dll"
          DestinationFolder="$(SolutionDir)AgentCoreProcessor\bin\$(Configuration)\net8.0\Plugins\" />
  </Target>

</Project>
```

## 2. Global 组件示例（Hello 工具）

Global 组件全局只有一个实例，适合不需要按循环隔离状态的工具。

**MyFirstComponent.cs**：

```csharp
using AgentLilara.PluginSDK;

namespace Plugin.MyFirst;

[Component(Name = "my-first", Scope = ComponentScope.Global)]
[ToolVisibility(Default = Visibility.FollowState)]
public class MyFirstComponent : GlobalComponentBase
{
    private readonly List<ITool> _tools = new();

    public override ComponentMeta Meta => new()
    {
        Name = "my-first",
        Description = "示例插件——打招呼",
        DefaultEnabled = true,
        PromptPriority = 50      // prompt 注入优先级，越小越靠前
    };

    public override IEnumerable<ITool> Tools => _tools;

    public override Task OnInitAsync(IGlobalComponentContext context, InitReason reason)
    {
        // 使用 IPluginStorage 构造工具，自动绑定到本插件的隔离目录
        _tools.Add(new HelloTool(context.Storage));
        return Task.CompletedTask;
    }
}
```

**HelloTool.cs**：

```csharp
using AgentLilara.PluginSDK;

namespace Plugin.MyFirst;

[ToolMeta(
    Group = "hello",                    // 工具组名（WebUI 分组显示）
    ContinueLoop = true,                // 执行后触发下一轮 AI
    CapabilitySummary = "打招呼工具",    // Express 模式下展示
    Permission = ToolPermission.Default,
    ExpressAvailable = true             // 可在 Express 模式使用
)]
public class HelloTool : ITool
{
    public string Name => "say_hello";
    public string Description => "向指定的人打招呼。";
    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("name", "要打招呼的人的名字", 0),
        new("language", "语言（zh/en），默认 zh", 1, isRequired: false)
    ];
    public TimeSpan Timeout => TimeSpan.FromSeconds(5);

    // Component 模式的构造函数：接收 IPluginStorage
    public HelloTool(IPluginStorage storage)
    {
        // 可用 storage.GlobalDirectory / storage.InstanceDirectory 读写文件
    }

    public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
    {
        var name = resolvedInputs.Count > 0 ? resolvedInputs[0].Trim() : "世界";
        var lang = resolvedInputs.Count > 1 ? resolvedInputs[1].Trim() : "zh";

        var greeting = lang == "en" ? $"Hello, {name}!" : $"你好，{name}！";
        return Task.FromResult(new ToolResult { Status = "success", Data = greeting });
    }
}
```

## 3. Loop 组件示例（计数器）

Loop 组件每个引擎循环一个实例，适合需要按循环隔离状态的工具。

**CounterComponent.cs**：

```csharp
using AgentLilara.PluginSDK;

namespace Plugin.MyFirst;

[Component(Name = "counter", Scope = ComponentScope.Loop)]
[LoopApplicability(Channel = Applicability.Enabled, System = Applicability.Disabled)]
[ToolVisibility(Default = Visibility.AlwaysVisible)]
public class CounterComponent : LoopComponentBase
{
    private CounterTool? _counter;

    public override ComponentMeta Meta => new()
    {
        Name = "counter",
        Description = "计数器示例",
        DefaultEnabled = true,
        PromptPriority = 50
    };

    public override IEnumerable<ITool> Tools
    {
        get
        {
            if (_counter != null) yield return _counter;
        }
    }

    public override Task OnInitAsync(ILoopComponentContext context, InitReason reason)
    {
        _counter = new CounterTool(context.Storage);
        return Task.CompletedTask;
    }

    // 可选：注入 prompt 片段
    public override string? BuildPromptSection()
    {
        return _counter?.BuildSection();
    }
}
```

**CounterTool.cs**：

```csharp
using AgentLilara.PluginSDK;

namespace Plugin.MyFirst;

[ToolMeta(Group = "counter", ContinueLoop = true)]
public class CounterTool : ITool
{
    private int _count = 0;

    public string Name => "counter";
    public string Description => "计数器：action: increment(加一) / get(查看) / reset(清零)。";
    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("action", "操作类型：increment / get / reset", 0)
    ];
    public TimeSpan Timeout => TimeSpan.FromSeconds(3);

    public CounterTool(IPluginStorage storage) { }

    public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
    {
        var action = resolvedInputs.Count > 0 ? resolvedInputs[0].Trim() : "get";
        switch (action)
        {
            case "increment":
                _count++;
                return Ok($"已加一，当前值: {_count}");
            case "reset":
                _count = 0;
                return Ok("已清零");
            default:
                return Ok($"当前值: {_count}");
        }
    }

    public string? BuildSection() => _count > 0 ? $"[计数器] 当前值: {_count}" : null;

    private static Task<ToolResult> Ok(string data) =>
        Task.FromResult(new ToolResult { Status = "success", Data = data });
}
```

## 4. 无组件的独立工具

简单场景可以不定义组件，直接暴露 `ITool` 实现。PluginLoader 会自动发现并直接注册。

```csharp
namespace Plugin.MyFirst;

[ToolMeta(Group = "utils", ContinueLoop = true)]
public class EchoTool : ITool
{
    public string Name => "echo";
    public string Description => "回显输入的内容。";

    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("text", "要回显的文本", 0)
    ];

    public TimeSpan Timeout => TimeSpan.FromSeconds(3);

    // 独立工具模式：构造函数接收 IToolContext
    public EchoTool(IToolContext ctx) { }

    public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
    {
        var text = resolvedInputs.Count > 0 ? resolvedInputs[0] : "";
        return Task.FromResult(new ToolResult { Status = "success", Data = text });
    }
}
```

## 5. 验证

1. 编译整个 solution：`dotnet build`
2. 启动宿主：`dotnet run --project AgentCoreProcessor`
3. 打开 WebUI `http://localhost:5000` → 插件管理页面（`/p/plugins`）
4. 确认 "my-first" / "counter" 组件出现在列表中并已启用
5. 点击 "热重载" 按钮可重新加载所有插件
