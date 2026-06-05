using System.Text.Json;
using AgentLilara.PluginSDK;

namespace Plugin.WebSearch;

[ToolMeta(Group = null, ContinueLoop = true, ExpressAvailable = true, OutputOnly = false)]
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
            return new ToolResult { Status = "success", Data = JsonSerializer.Serialize(results) };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new ToolResult { Status = "failed", Error = "搜索超时" };
        }
        catch (Exception ex)
        {
            return new ToolResult { Status = "failed", Error = $"搜索失败: {ex.Message}" };
        }
    }
}
