using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.MemoryTools;

[ToolMeta(ContinueLoop = true, CapabilitySummary = "记忆存储：写入主记忆库或临时记忆库")]
public class MemoryStoreTool : ITool
{
    private readonly IMemoryAccess? memory;

    public string Name => "memory_store";
    public string Description => "写入一条记忆。target=main 存入主记忆库（持久化），target=temp 存入临时记忆库（短期缓存）。";
    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("target", "目标库：main 或 temp", 0),
        new("content", "记忆内容", 1),
        new("type", "（可选）类型：knowledge / fact / feedback / inference / event", 2, false),
        new("subject", "（可选）主题标签", 3, false),
        new("person_id", "（可选）关联人物 ID", 4, false),
        new("channel_id", "（可选）关联频道 ID", 5, false),
        new("importance", "（可选）重要性 0.0-1.0，默认 0.5", 6, false),
        new("confidence", "（可选）可信度：high / low，默认 high", 7, false),
        new("is_persistent", "（可选）是否持久化：true / false，默认 true", 8, false),
        new("expires_at", "（可选）过期时间 ISO 8601，如 2026-06-01T00:00:00", 9, false)
    ];
    public TimeSpan Timeout => TimeSpan.FromSeconds(30);

    public MemoryStoreTool(IMemoryAccess? memoryAccess)
    {
        memory = memoryAccess;
    }

    public async Task<ToolResult> ExecuteAsync(List<string> inputs, CancellationToken ct)
    {
        if (memory == null)
            return Fail("记忆服务不可用");

        if (inputs.Count < 2 || string.IsNullOrWhiteSpace(inputs[1]))
            return Fail("content 不能为空");

        var target = inputs[0].Trim().ToLower();
        var content = inputs[1].Trim();
        var type = GetArg(inputs, 2);
        var subject = GetArg(inputs, 3);

        if (target == "temp")
        {
            var req = new TempMemoryWriteRequest
            {
                Content = content,
                Type = type,
                Subject = subject,
                PersonId = ParseInt(GetArg(inputs, 4)),
                ChannelId = ParseInt(GetArg(inputs, 5))
            };
            var id = await memory.StoreTempAsync(req);
            return Ok($"已存入临时记忆库 (id={id})");
        }

        if (target != "main")
            return Fail("target 必须为 main 或 temp");

        var mainReq = new MemoryWriteRequest
        {
            Content = content,
            Type = type,
            Subject = subject,
            PersonId = ParseInt(GetArg(inputs, 4)),
            ChannelId = ParseInt(GetArg(inputs, 5)),
            Importance = ParseFloat(GetArg(inputs, 6)) ?? 0.5f,
            Confidence = GetArg(inputs, 7) ?? "high",
            IsPersistent = GetArg(inputs, 8)?.ToLower() != "false",
            ExpiresAt = ParseDateTime(GetArg(inputs, 9))
        };
        var mainId = await memory.StoreAsync(mainReq);
        return Ok($"已存入主记忆库 (id={mainId})");
    }

    private static string? GetArg(List<string> inputs, int index)
        => inputs.Count > index && !string.IsNullOrWhiteSpace(inputs[index]) ? inputs[index].Trim() : null;

    private static int? ParseInt(string? s)
        => int.TryParse(s, out var v) ? v : null;

    private static float? ParseFloat(string? s)
        => float.TryParse(s, out var v) ? v : null;

    private static DateTime? ParseDateTime(string? s)
        => DateTime.TryParse(s, out var v) ? v : null;

    private static ToolResult Ok(string data) => new() { Status = "success", Data = data };
    private static ToolResult Fail(string err) => new() { Status = "failed", Error = err };
}
