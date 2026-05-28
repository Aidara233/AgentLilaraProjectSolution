# Plugin.WebSearch Implementation Plan

> **状态：已完成 (2026-05-27)** — 所有功能已实现

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 为 Agent 提供网页搜索能力，ISearchBackend 统一接口 + Tavily 首个实现

**Architecture:** 独立插件，Global+Loop 双组件 + WebSearchAccessor 静态桥接，`web_search` 单一工具同步返回结果

**Tech Stack:** .NET 8, System.Net.Http, System.Text.Json, AgentLilara.PluginSDK

---

### Task 1: 创建项目文件

**Files:**
- Create: `Plugins/Plugin.WebSearch/Plugin.WebSearch.csproj`

- [ ] **Step 1: 创建 csproj**

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <RootNamespace>Plugin.WebSearch</RootNamespace>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\AgentLilara.PluginSDK\AgentLilara.PluginSDK.csproj">
      <Private>false</Private>
    </ProjectReference>
  </ItemGroup>

  <Target Name="CopyToHostPlugins" AfterTargets="Build">
    <Copy SourceFiles="$(OutputPath)Plugin.WebSearch.dll"
          DestinationFolder="$(SolutionDir)AgentCoreProcessor\bin\$(Configuration)\net8.0\Plugins\" />
  </Target>

</Project>
```

- [ ] **Step 2: Commit**

```bash
cd "E:/Workspace/AgentLilaraProject/AgentLilaraProjectSolution"
git add Plugins/Plugin.WebSearch/Plugin.WebSearch.csproj
git commit -m "feat: add Plugin.WebSearch project file"
```

---

### Task 2: 搜索接口与数据模型

**Files:**
- Create: `Plugins/Plugin.WebSearch/ISearchBackend.cs`

- [ ] **Step 1: 创建接口和模型**

```csharp
namespace Plugin.WebSearch;

public interface ISearchBackend
{
    Task<SearchResults> SearchAsync(SearchRequest request, CancellationToken ct);
}

public class SearchRequest
{
    public string Query { get; set; } = "";
    public int Count { get; set; } = 5;
    public bool IncludeAnswer { get; set; }
    public bool IncludeRawContent { get; set; }
    public string? Topic { get; set; }
}

public class SearchResults
{
    public string Query { get; set; } = "";
    public string? Answer { get; set; }
    public List<SearchResultItem> Results { get; set; } = new();
}

public class SearchResultItem
{
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
    public string Content { get; set; } = "";
    public double Score { get; set; }
    public string? RawContent { get; set; }
}
```

- [ ] **Step 2: Commit**

```bash
cd "E:/Workspace/AgentLilaraProject/AgentLilaraProjectSolution"
git add Plugins/Plugin.WebSearch/ISearchBackend.cs
git commit -m "feat: add ISearchBackend interface and search data models"
```

---

### Task 3: 配置加载

**Files:**
- Create: `Plugins/Plugin.WebSearch/WebSearchConfig.cs`

- [ ] **Step 1: 创建配置类**

```csharp
using System.Text.Json;

namespace Plugin.WebSearch;

public class WebSearchConfig
{
    public string Backend { get; set; } = "tavily";
    public TavilyConfig Tavily { get; set; } = new();

    public static WebSearchConfig Load(string configDir)
    {
        Directory.CreateDirectory(configDir);
        var path = Path.Combine(configDir, "WebSearch.json");

        if (!File.Exists(path))
        {
            var cfg = new WebSearchConfig();
            var json = JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
            return cfg;
        }

        var text = File.ReadAllText(path);
        return JsonSerializer.Deserialize<WebSearchConfig>(text,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
    }
}

public class TavilyConfig
{
    public string ApiKey { get; set; } = "";
    public string BaseUrl { get; set; } = "https://api.tavily.com/search";
    public string SearchDepth { get; set; } = "basic";
    public List<string> IncludeDomains { get; set; } = new();
    public List<string> ExcludeDomains { get; set; } = new();
    public int TimeoutSeconds { get; set; } = 30;
}
```

- [ ] **Step 2: Commit**

```bash
cd "E:/Workspace/AgentLilaraProject/AgentLilaraProjectSolution"
git add Plugins/Plugin.WebSearch/WebSearchConfig.cs
git commit -m "feat: add WebSearchConfig with Tavily settings"
```

---

### Task 4: Tavily 后端实现

**Files:**
- Create: `Plugins/Plugin.WebSearch/TavilySearchBackend.cs`

- [ ] **Step 1: 实现 TavilySearchBackend**

```csharp
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Plugin.WebSearch;

public class TavilySearchBackend : ISearchBackend
{
    private readonly HttpClient _http;
    private readonly TavilyConfig _config;

    public TavilySearchBackend(HttpClient http, TavilyConfig config)
    {
        _http = http;
        _config = config;
    }

    public async Task<SearchResults> SearchAsync(SearchRequest request, CancellationToken ct)
    {
        var body = new
        {
            api_key = _config.ApiKey,
            query = request.Query,
            max_results = request.Count,
            search_depth = _config.SearchDepth,
            include_answer = request.IncludeAnswer,
            include_raw_content = request.IncludeRawContent,
            topic = request.Topic ?? "general",
            include_domains = _config.IncludeDomains,
            exclude_domains = _config.ExcludeDomains
        };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(_config.TimeoutSeconds));

        var response = await _http.PostAsJsonAsync(_config.BaseUrl, body, cts.Token);
        response.EnsureSuccessStatusCode();

        var raw = await response.Content.ReadFromJsonAsync<TavilyResponse>(
            cancellationToken: cts.Token);
        if (raw == null)
            throw new Exception("Tavily 返回空响应");

        return new SearchResults
        {
            Query = raw.Query ?? request.Query,
            Answer = raw.Answer,
            Results = raw.Results?.Select(r => new SearchResultItem
            {
                Title = r.Title ?? "",
                Url = r.Url ?? "",
                Content = r.Content ?? "",
                Score = r.Score,
                RawContent = r.RawContent
            }).ToList() ?? new()
        };
    }

    private class TavilyResponse
    {
        [JsonPropertyName("query")]
        public string? Query { get; set; }

        [JsonPropertyName("answer")]
        public string? Answer { get; set; }

        [JsonPropertyName("results")]
        public List<TavilyResult>? Results { get; set; }
    }

    private class TavilyResult
    {
        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("url")]
        public string? Url { get; set; }

        [JsonPropertyName("content")]
        public string? Content { get; set; }

        [JsonPropertyName("score")]
        public double Score { get; set; }

        [JsonPropertyName("raw_content")]
        public string? RawContent { get; set; }
    }
}
```

- [ ] **Step 2: Commit**

```bash
cd "E:/Workspace/AgentLilaraProject/AgentLilaraProjectSolution"
git add Plugins/Plugin.WebSearch/TavilySearchBackend.cs
git commit -m "feat: add TavilySearchBackend implementation"
```

---

### Task 5: WebSearchTool 工具

**Files:**
- Create: `Plugins/Plugin.WebSearch/WebSearchTool.cs`

- [ ] **Step 1: 创建工具**

```csharp
using AgentLilara.PluginSDK;

namespace Plugin.WebSearch;

[ToolMeta(Group = null, ContinueLoop = true, ExpressAvailable = false, OutputOnly = false)]
public class WebSearchTool : ITool
{
    private readonly ISearchBackend? _backend;

    public WebSearchTool() { }

    public WebSearchTool(ISearchBackend backend)
    {
        _backend = backend;
    }

    public string Name => "web_search";
    public string Description => "搜索网页，返回相关结果列表。可通过 count 控制返回数量（1-10），include_answer 获取AI摘要，include_raw_content 获取原始网页内容，topic 设为 news 搜索新闻。";

    public IReadOnlyList<ToolParameter> Parameters => new List<ToolParameter>
    {
        new("query", "搜索关键词", 0),
        new("count", "返回结果数量（默认5，最大10）", 1, isRequired: false),
        new("include_answer", "是否包含AI摘要（默认false）", 2, isRequired: false),
        new("include_raw_content", "是否包含原始内容（默认false）", 3, isRequired: false),
        new("topic", "主题：general 或 news（默认general）", 4, isRequired: false)
    };

    public TimeSpan Timeout => TimeSpan.FromSeconds(35);

    public async Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
    {
        if (_backend == null)
            return new ToolResult { Status = "failed", Error = "搜索服务不可用" };

        var query = resolvedInputs.Count > 0 ? resolvedInputs[0].Trim() : "";
        if (string.IsNullOrEmpty(query))
            return new ToolResult { Status = "failed", Error = "query 不能为空" };

        var count = 5;
        if (resolvedInputs.Count > 1 && int.TryParse(resolvedInputs[1], out var c))
            count = Math.Clamp(c, 1, 10);

        var includeAnswer = false;
        if (resolvedInputs.Count > 2 && bool.TryParse(resolvedInputs[2], out var ia))
            includeAnswer = ia;

        var includeRawContent = false;
        if (resolvedInputs.Count > 3 && bool.TryParse(resolvedInputs[3], out var rc))
            includeRawContent = rc;

        var topic = "general";
        if (resolvedInputs.Count > 4)
        {
            var t = resolvedInputs[4].Trim().ToLowerInvariant();
            if (t == "news") topic = "news";
        }

        try
        {
            var request = new SearchRequest
            {
                Query = query,
                Count = count,
                IncludeAnswer = includeAnswer,
                IncludeRawContent = includeRawContent,
                Topic = topic
            };

            var results = await _backend.SearchAsync(request, ct);
            return new ToolResult { Status = "success", Data = results };
        }
        catch (OperationCanceledException)
        {
            return new ToolResult { Status = "failed", Error = "搜索超时" };
        }
        catch (Exception ex)
        {
            return new ToolResult { Status = "failed", Error = $"搜索失败: {ex.Message}" };
        }
    }
}
```

- [ ] **Step 2: Commit**

```bash
cd "E:/Workspace/AgentLilaraProject/AgentLilaraProjectSolution"
git add Plugins/Plugin.WebSearch/WebSearchTool.cs
git commit -m "feat: add WebSearchTool implementation"
```

---

### Task 6: Global 组件 + Accessor

**Files:**
- Create: `Plugins/Plugin.WebSearch/WebSearchGlobalComponent.cs`

- [ ] **Step 1: 创建 Global 组件和 Accessor**

```csharp
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.WebSearch;

[Component(Name = "web-search-global", Scope = ComponentScope.Global)]
public class WebSearchGlobalComponent : GlobalComponentBase
{
    private IGlobalComponentContext _ctx = null!;
    private HttpClient _http = null!;

    public override ComponentMeta Meta => new()
    {
        Name = "web-search-global",
        Description = "网页搜索全局组件：HttpClient + 搜索后端管理",
        DefaultEnabled = true,
        PromptPriority = 99
    };

    public override IEnumerable<ITool> Tools => Array.Empty<ITool>();

    public override Task OnInitAsync(IGlobalComponentContext context, InitReason reason)
    {
        _ctx = context;

        // 加载配置
        var configDir = Path.Combine(context.Storage.GlobalDirectory, "..");
        var config = WebSearchConfig.Load(Path.GetFullPath(configDir));

        // 创建 HttpClient
        _http = new HttpClient();
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("AgentLilara-WebSearch/1.0");

        // 根据配置选择后端
        ISearchBackend backend = config.Backend.ToLowerInvariant() switch
        {
            "tavily" => new TavilySearchBackend(_http, config.Tavily),
            _ => throw new Exception($"未知搜索后端: {config.Backend}")
        };

        WebSearchAccessor.Configure(_http, backend);

        return Task.CompletedTask;
    }

    public override Task OnShutdownAsync(ShutdownReason reason)
    {
        _http?.Dispose();
        return Task.CompletedTask;
    }
}

/// <summary>
/// 静态访问器：Global组件初始化后设置，Loop组件工具通过此访问共享资源。
/// </summary>
public static class WebSearchAccessor
{
    public static HttpClient? HttpClient { get; private set; }
    public static ISearchBackend? Backend { get; private set; }

    public static void Configure(HttpClient http, ISearchBackend backend)
    {
        HttpClient = http;
        Backend = backend;
    }
}
```

- [ ] **Step 2: Commit**

```bash
cd "E:/Workspace/AgentLilaraProject/AgentLilaraProjectSolution"
git add Plugins/Plugin.WebSearch/WebSearchGlobalComponent.cs
git commit -m "feat: add WebSearchGlobalComponent and WebSearchAccessor"
```

---

### Task 7: Loop 组件

**Files:**
- Create: `Plugins/Plugin.WebSearch/WebSearchLoopComponent.cs`

- [ ] **Step 1: 创建 Loop 组件**

```csharp
using AgentLilara.PluginSDK;

namespace Plugin.WebSearch;

[Component(Name = "web-search", Scope = ComponentScope.Loop)]
[LoopApplicability(Channel = Applicability.Enabled, System = Applicability.Enabled,
    SubAgent = Applicability.Enabled, Review = Applicability.Disabled)]
public class WebSearchLoopComponent : LoopComponentBase
{
    private WebSearchTool? _search;

    public override ComponentMeta Meta => new()
    {
        Name = "web-search",
        Description = "网页搜索工具",
        DefaultEnabled = true,
        PromptPriority = 90
    };

    public override IEnumerable<ITool> Tools
    {
        get
        {
            if (_search != null) yield return _search;
        }
    }

    public override Task OnInitAsync(ILoopComponentContext context, InitReason reason)
    {
        var backend = WebSearchAccessor.Backend
            ?? throw new InvalidOperationException("WebSearchGlobalComponent 未初始化");

        _search = new WebSearchTool(backend);
        return Task.CompletedTask;
    }
}
```

- [ ] **Step 2: Commit**

```bash
cd "E:/Workspace/AgentLilaraProject/AgentLilaraProjectSolution"
git add Plugins/Plugin.WebSearch/WebSearchLoopComponent.cs
git commit -m "feat: add WebSearchLoopComponent"
```

---

### Task 8: 注册到解决方案

**Files:**
- Modify: `AgentLilaraProjectSolution.sln`

需要读取现有 .sln 文件，在 Plugin.NetworkTools 项目附近插入新项目配置行。

- [ ] **Step 1: 在 .sln 中添加项目引用**

读取 .sln 文件，找到 `Plugin.NetworkTools` 项目块（GUID 为 `{DAEFD6E7-2FCC-4169-9857-514353733A34}`），在其后插入 Plugin.WebSearch 项目块（新 GUID），并将新 GUID 加入 `NestedProjects` 段（parent GUID 为 Plugins 文件夹的 GUID）。

具体的 GUID 和插入位置需在编辑前读取 .sln 确认。

- [ ] **Step 2: 编译验证**

```bash
cd "E:/Workspace/AgentLilaraProject/AgentLilaraProjectSolution"
dotnet build
```

预期：0 errors, 0 warnings

- [ ] **Step 3: Commit**

```bash
cd "E:/Workspace/AgentLilaraProject/AgentLilaraProjectSolution"
git add AgentLilaraProjectSolution.sln
git commit -m "build: register Plugin.WebSearch in solution"
```
