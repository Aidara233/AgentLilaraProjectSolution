using AgentLilara.PluginSDK;

namespace Plugin.ReviewTools;

[ToolMeta(Group = "review")]
public class ReviewThinkingNotesTool : ITool
{
    public string Name => "review_thinking_notes";
    public string Description => "管理思考笔记。write 写入/覆盖，delete 删除，list 列出所有。笔记在复盘期间跨轮保持。";
    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("action", "操作：write / delete / list", 0),
        new("key", "笔记键名", 1, false),
        new("value", "笔记内容（write 时必填）", 2, false)
    ];
    public TimeSpan Timeout => TimeSpan.FromSeconds(5);

    public Task<ToolResult> ExecuteAsync(List<string> inputs, CancellationToken ct)
    {
        var action = inputs.Count > 0 ? inputs[0]?.Trim().ToLower() : null;

        return Task.FromResult(action switch
        {
            "write" => new ToolResult
            {
                Status = "success",
                Data = $"笔记已保存: {(inputs.Count > 1 ? inputs[1] : "")}"
            },
            "delete" => new ToolResult
            {
                Status = "success",
                Data = $"笔记已删除: {(inputs.Count > 1 ? inputs[1] : "")}"
            },
            "list" => new ToolResult
            {
                Status = "success",
                Data = "（笔记内容保存在对话历史中，请回顾之前的工具调用结果）"
            },
            _ => new ToolResult { Status = "failed", Error = "action 必须是 write/delete/list" }
        });
    }
}
