using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.ReviewTools;

[ToolMeta(Group = "review")]
public class ReviewSearchMemoryTool : ITool
{
    private readonly IMemoryAccess _memory;

    public ReviewSearchMemoryTool(IToolContext ctx) => _memory = ctx.Require<IMemoryAccess>();

    public string Name => "review_search_memory";
    public string Description => "语义搜索记忆库（返回 ID+内容+重要度+时间+PersonId）。写入前请先搜索确认无重复或高度相似的记忆。";
    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("query", "搜索关键词或语义描述", 0),
        new("person_id", "限定人物ID", 1, false),
        new("limit", "返回条数（默认10）", 2, false)
    ];
    public TimeSpan Timeout => TimeSpan.FromSeconds(30);

    public async Task<ToolResult> ExecuteAsync(List<string> inputs, CancellationToken ct)
    {
        var query = inputs.Count > 0 ? inputs[0] : null;
        if (string.IsNullOrWhiteSpace(query))
            return new ToolResult { Status = "failed", Error = "query 不能为空" };

        int? personId = inputs.Count > 1 && int.TryParse(inputs[1], out var pid) ? pid : null;
        int limit = inputs.Count > 2 && int.TryParse(inputs[2], out var lim) ? lim : 10;

        var results = await _memory.SemanticSearchAsync(query, limit, personId);
        if (results.Count == 0)
            return new ToolResult { Status = "success", Data = "未找到相关记忆。" };

        var lines = results.Select(m =>
            $"[ID:{m.Id}] (重要度:{m.Importance:F1}, 相似度:{m.Score:F2}) P#{m.PersonId ?? 0} {m.Content}");
        return new ToolResult { Status = "success", Data = string.Join("\n", lines) };
    }
}
