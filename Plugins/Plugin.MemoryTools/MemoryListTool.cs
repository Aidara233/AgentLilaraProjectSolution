using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.MemoryTools;

[ToolMeta(ContinueLoop = true, CapabilitySummary = "记忆列表：条件筛选和分页浏览记忆")]
public class MemoryListTool : ITool
{
    private readonly IMemoryAccess? memory;

    public string Name => "memory_list";
    public string Description => "条件筛选和分页浏览主记忆库。所有参数均可选，不传则不限制。";
    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("person_id", "（可选）按人物 ID 过滤", 0, false),
        new("channel_id", "（可选）按频道 ID 过滤", 1, false),
        new("type", "（可选）按类型过滤：knowledge / fact / feedback / inference / event", 2, false),
        new("subject", "（可选）按主题标签过滤", 3, false),
        new("keyword", "（可选）内容关键词模糊匹配", 4, false),
        new("created_after", "（可选）创建时间起点 ISO 8601", 5, false),
        new("created_before", "（可选）创建时间终点 ISO 8601", 6, false),
        new("min_importance", "（可选）最低重要性 0.0-1.0", 7, false),
        new("offset", "（可选）分页偏移，默认 0", 8, false),
        new("limit", "（可选）最大返回条数，默认 50", 9, false)
    ];
    public TimeSpan Timeout => TimeSpan.FromSeconds(15);

    public MemoryListTool(IMemoryAccess? memoryAccess)
    {
        memory = memoryAccess;
    }

    public async Task<ToolResult> ExecuteAsync(List<string> inputs, CancellationToken ct)
    {
        if (memory == null)
            return Fail("记忆服务不可用");

        var filter = new MemoryFilter
        {
            PersonId = ParseInt(inputs, 0),
            ChannelId = ParseInt(inputs, 1),
            Type = GetArg(inputs, 2),
            Subject = GetArg(inputs, 3),
            KeywordContains = GetArg(inputs, 4),
            CreatedAfter = ParseDateTime(GetArg(inputs, 5)),
            CreatedBefore = ParseDateTime(GetArg(inputs, 6)),
            MinImportance = ParseFloat(GetArg(inputs, 7)),
            Offset = ParseInt(inputs, 8) ?? 0,
            Limit = ParseInt(inputs, 9) ?? 50
        };

        var results = await memory.FilterAsync(filter);
        if (results.Count == 0)
            return Ok("无匹配记忆");

        var sb = new StringBuilder();
        sb.AppendLine($"共 {results.Count} 条 (offset={filter.Offset}, limit={filter.Limit}):");
        foreach (var m in results)
        {
            var ids = new List<string>();
            if (m.PersonId.HasValue) ids.Add($"p={m.PersonId}");
            if (m.ChannelId.HasValue) ids.Add($"ch={m.ChannelId}");
            var tags = ids.Count > 0 ? $" [{string.Join(", ", ids)}]" : "";

            sb.AppendLine($"[{m.Id}] (type={m.Type}, imp={m.Importance:F1}, conf={m.Confidence}){tags} {m.Content}");
            if (m.Subject != null) sb.AppendLine($"    subject: {m.Subject}");
        }
        return Ok(sb.ToString().TrimEnd());
    }

    private static string? GetArg(List<string> inputs, int index)
        => inputs.Count > index && !string.IsNullOrWhiteSpace(inputs[index]) ? inputs[index].Trim() : null;

    private static int? ParseInt(List<string> inputs, int index)
        => inputs.Count > index && int.TryParse(inputs[index], out var v) ? v : null;

    private static float? ParseFloat(string? s)
        => float.TryParse(s, out var v) ? v : null;

    private static DateTime? ParseDateTime(string? s)
        => DateTime.TryParse(s, out var v) ? v : null;

    private static ToolResult Ok(string data) => new() { Status = "success", Data = data };
    private static ToolResult Fail(string err) => new() { Status = "failed", Error = err };
}
