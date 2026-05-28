# Plugin.WebSearch — 网页搜索插件

> **状态：已完成 (2026-05-27)** — 所有功能已实现

## Summary

为 Agent 提供网页搜索能力。独立插件，不依赖 NetworkTools。ISearchBackend 统一后端接口，Tavily 作为首个实现，后续可通过修改配置切换后端。

## 工具清单

| 工具 | 说明 |
|---|---|
| `web_search` | 调用搜索后端，返回结构化结果列表 |

## 组件结构

### WebSearchLoopComponent（Loop 组件）

- **适用循环**：Channel / System / SubAgent（默认全部启用）
- **职责**：创建 WebSearchTool 并注册

生命周期：
```
OnInitAsync → 从 Accessor 拿 Backend → 创建 WebSearchTool
Tools → yield return _search
```

### WebSearchGlobalComponent（Global 组件）

- **职责**：加载配置、创建 HttpClient、实例化 ISearchBackend
- **单实例**：持有 HttpClient 和 Backend 实例

生命周期：
```
OnInitAsync → 加载 WebSearch.json → 创建 HttpClient → 根据 backend 字段选择实现 → 设入 Accessor
OnShutdownAsync → 释放 HttpClient
```

## 工具详细定义

### web_search

```
参数:
  query              string  搜索关键词（必填）
  count              number  返回结果数量，默认 5，最大 10（可选）
  include_answer     bool    是否包含 AI 摘要，默认 false（可选）
  include_raw_content bool   是否包含原始内容，默认 false（可选）
  topic              string  主题：general / news，默认 general（可选）

返回:
  query              搜索关键词
  answer             AI 摘要（仅 include_answer=true 时）
  results            结果列表，每项含:
    - title          标题
    - url            链接
    - content        内容摘要
    - score          相关性评分
    - raw_content    原始内容（仅 include_raw_content=true 时）
  count              返回结果数
```

## ISearchBackend 接口

```csharp
public interface ISearchBackend
{
    Task<SearchResults> SearchAsync(SearchRequest request, CancellationToken ct);
}

public class SearchRequest
{
    public string Query { get; set; }
    public int Count { get; set; }
    public bool IncludeAnswer { get; set; }
    public bool IncludeRawContent { get; set; }
    public string? Topic { get; set; }
}

public class SearchResults
{
    public string Query { get; set; }
    public string? Answer { get; set; }       // AI 摘要
    public List<SearchResultItem> Results { get; set; }
}

public class SearchResultItem
{
    public string Title { get; set; }
    public string Url { get; set; }
    public string Content { get; set; }        // 摘要
    public double Score { get; set; }
    public string? RawContent { get; set; }    // 原始内容（可选）
}
```

## TavilyBackend 实现

直接调用 Tavily Search API（POST `https://api.tavily.com/search`），用 System.Text.Json 收发 JSON。

请求体映射：
```
query          → api_key + query
count          → max_results
include_answer → include_answer
include_raw_content → include_raw_content
topic          → topic (general/news)
search_depth   → 写死 basic（配置可调）
include_domains/exclude_domains → 写死空（配置可调）
```

响应映射到 SearchResults，失败时抛异常由工具层捕获转换为 ToolResult。

## 配置结构

`Storage/Plugin/WebSearch.json`（首次运行时自动生成）：

```json
{
  "backend": "tavily",
  "tavily": {
    "apiKey": "",
    "baseUrl": "https://api.tavily.com/search",
    "searchDepth": "basic",
    "includeDomains": [],
    "excludeDomains": [],
    "timeoutSeconds": 30
  }
}
```

WebSearchConfig 类负责加载/保存，类比 NetworkTools 的 SecurityConfig.Load()。

## 错误处理

| 场景 | 行为 |
|---|---|
| API key 未配置 | 返回 failed，提示配置 apiKey |
| 搜索超时 | 返回 failed，提示超时 |
| API 返回错误 | 返回 failed，附带错误信息 |
| 网络异常 | 返回 failed，附带异常消息 |
| 无结果 | 返回 success，results 为空列表 |

## Accessor 静态桥接

```csharp
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

## 文件清单

```
Plugins/Plugin.WebSearch/
├── Plugin.WebSearch.csproj
├── WebSearchConfig.cs            # SecurityConfig 加载/保存
├── ISearchBackend.cs             # 接口 + SearchRequest/SearchResults/SearchResultItem
├── TavilySearchBackend.cs        # Tavily 实现
├── WebSearchTool.cs              # ITool 实现
├── WebSearchGlobalComponent.cs   # Global 组件 + WebSearchAccessor
├── WebSearchLoopComponent.cs     # Loop 组件
```

- 零第三方依赖（仅 System.Net.Http + System.Text.Json）
- .NET 8, 引用 AgentLilara.PluginSDK（Private=false）
- CopyToHostPlugins 编译后复制
- 需在 .sln 中注册新项目
