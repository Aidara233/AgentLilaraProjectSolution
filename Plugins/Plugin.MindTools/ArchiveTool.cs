using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.MindTools;

/// <summary>
/// archive 工具：归档旧记忆（维护/清理）。
/// 注意：归档语义待定（06-pending），首版为"检索 + 标记处理完成"的轻量实现，不做删除。
/// 真实归档由维护引擎（MaintenanceEngine）执行，本工具仅做检索统计并返回提示。
/// </summary>
[ToolMeta(Group = "mind")]
public class ArchiveTool : ITool
{
    private readonly IToolContext _ctx;

    public ArchiveTool(IToolContext ctx) => _ctx = ctx;

    public string Name => "archive";
    public string Description => "归档旧记忆（维护/清理）。首版仅做检索统计，不删除记忆；真实归档由维护引擎完成。";

    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("query", "归档条件关键词", 0, false),
        new("limit", "本次归档条数上限（默认20）", 1, false)
    ];

    public TimeSpan Timeout => TimeSpan.FromSeconds(15);

    public async Task<ToolResult> ExecuteAsync(List<string> inputs, CancellationToken ct)
    {
        // 获取检索服务；心智引擎未启动时 Require 会抛异常，捕获后返回 failed
        IRetrieveAccess retrieve;
        try
        {
            retrieve = _ctx.Require<IRetrieveAccess>();
        }
        catch (Exception)
        {
            return new ToolResult { Status = "failed", Error = "检索服务不可用" };
        }

        var query = inputs.Count > 0 ? (inputs[0] ?? "").Trim() : "";
        int limit = inputs.Count > 1 && int.TryParse(inputs[1], out var l) ? l : 20;
        if (limit <= 0) limit = 20;

        // query 为可选参数，但为空时没有检索条件，给出提示
        if (string.IsNullOrEmpty(query))
            return new ToolResult { Status = "success", Data = "未提供归档条件（query），本次未执行归档检索。请用 query 指定归档关键词。" };

        try
        {
            var result = await retrieve.SearchAsync(query, limit);
            var count = result.Hits.Count;

            // 首版仅统计：真实归档由 MaintenanceEngine 完成，本工具不删除任何记忆
            return new ToolResult
            {
                Status = "success",
                Data = $"已归档 {count} 条匹配（条件：{query}，上限 {limit}）。\n提示：归档语义待定（06-pending），首版仅做检索统计，真实归档由维护引擎（MaintenanceEngine）执行，未删除任何记忆。"
            };
        }
        catch (Exception ex)
        {
            return new ToolResult { Status = "failed", Error = $"检索失败: {ex.Message}" };
        }
    }
}
