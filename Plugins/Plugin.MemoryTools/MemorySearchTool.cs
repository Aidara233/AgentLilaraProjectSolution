using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.MemoryTools;

[ToolMeta(ContinueLoop = true, CapabilitySummary = "记忆搜索：语义搜索记忆（支持主库/临时库/两者）")]
public class MemorySearchTool : ITool
{
    private readonly IMemoryAccess? memory;

    public string Name => "memory_search";
    public string Description => "语义搜索记忆。scope=main 搜主库，scope=temp 搜临时库，scope=both 同时搜两者合并返回。";
    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("query", "搜索关键词", 0),
        new("scope", "（可选）搜索范围：main / temp / both，默认 main", 1, false),
        new("person_id", "（可选）按人物 ID 过滤", 2, false),
        new("channel_id", "（可选）按频道 ID 过滤", 3, false),
        new("limit", "（可选）最大返回条数，默认 10", 4, false)
    ];
    public TimeSpan Timeout => TimeSpan.FromSeconds(30);

    public MemorySearchTool(IMemoryAccess? memoryAccess)
    {
        memory = memoryAccess;
    }

    public async Task<ToolResult> ExecuteAsync(List<string> inputs, CancellationToken ct)
    {
        if (memory == null)
            return Fail("记忆服务不可用");

        if (inputs.Count == 0 || string.IsNullOrWhiteSpace(inputs[0]))
            return Fail("query 不能为空");

        var query = inputs[0].Trim();
        var scope = inputs.Count > 1 ? (inputs[1].Trim().ToLower()) : "main";
        var personId = ParseInt(inputs, 2);
        var channelId = ParseInt(inputs, 3);
        var limit = ParseInt(inputs, 4) ?? 10;

        if (scope == "both")
        {
            var mainTask = memory.SemanticSearchAsync(query, limit, personId, channelId);
            var tempTask = memory.SearchTempAsync(query, limit);
            await Task.WhenAll(mainTask, tempTask);

            var results = new List<(string Prefix, float Score, string Content, string? Subject, string? Type)>();
            results.AddRange(mainTask.Result.Select(m => ("", m.Score, m.Content, m.Subject, m.Type)));
            results.AddRange(tempTask.Result.Select(m => ("temp:", m.Score, m.Content, m.Subject, m.Type)));
            var merged = results.OrderByDescending(r => r.Score).Take(limit).ToList();

            if (merged.Count == 0)
                return Ok("未找到相关记忆");

            var sb = new StringBuilder();
            foreach (var (prefix, score, content, subject, type) in merged)
            {
                sb.AppendLine($"[{prefix}] (score={score:F3}, type={type}) {content}");
                if (subject != null) sb.AppendLine($"    subject: {subject}");
            }
            return Ok(sb.ToString().TrimEnd());
        }

        if (scope == "temp")
        {
            var tempResults = await memory.SearchTempAsync(query, limit);
            if (tempResults.Count == 0)
                return Ok("临时记忆库中未找到相关记忆");

            var sb = new StringBuilder();
            foreach (var m in tempResults)
            {
                sb.AppendLine($"[temp:{m.Id}] (score={m.Score:F3}) {m.Content}");
                if (m.Subject != null) sb.AppendLine($"    subject: {m.Subject}");
            }
            return Ok(sb.ToString().TrimEnd());
        }

        if (scope != "main")
            return Fail("scope 必须为 main / temp / both");

        var mainResults = await memory.SemanticSearchAsync(query, limit, personId, channelId);
        if (mainResults.Count == 0)
            return Ok("未找到相关记忆");

        var msb = new StringBuilder();
        foreach (var m in mainResults)
        {
            msb.AppendLine($"[{m.Id}] (score={m.Score:F3}, type={m.Type}) {m.Content}");
            if (m.Subject != null) msb.AppendLine($"    subject: {m.Subject}");
        }
        return Ok(msb.ToString().TrimEnd());
    }

    private static int? ParseInt(List<string> inputs, int index)
        => inputs.Count > index && int.TryParse(inputs[index], out var v) ? v : null;

    private static ToolResult Ok(string data) => new() { Status = "success", Data = data };
    private static ToolResult Fail(string err) => new() { Status = "failed", Error = err };
}
