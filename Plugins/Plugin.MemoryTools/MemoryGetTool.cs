using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.MemoryTools;

[ToolMeta(ContinueLoop = true, CapabilitySummary = "记忆查询：按 ID 获取单条记忆")]
public class MemoryGetTool : ITool
{
    private readonly IMemoryAccess? memory;

    public string Name => "memory_get";
    public string Description => "按 ID 从主记忆库获取单条记忆的完整信息。";
    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("id", "记忆 ID", 0)
    ];
    public TimeSpan Timeout => TimeSpan.FromSeconds(10);

    public MemoryGetTool(IMemoryAccess? memoryAccess)
    {
        memory = memoryAccess;
    }

    public async Task<ToolResult> ExecuteAsync(List<string> inputs, CancellationToken ct)
    {
        if (memory == null)
            return Fail("记忆服务不可用");

        if (!int.TryParse(inputs[0], out var id))
            return Fail("id 必须为整数");

        var entry = await memory.GetByIdAsync(id);
        if (entry == null)
            return Ok($"未找到记忆 id={id}");

        var expires = entry.ExpiresAt?.ToString("O") ?? "无";
        return Ok(
            $"ID: {entry.Id}\n" +
            $"内容: {entry.Content}\n" +
            $"类型: {entry.Type ?? "无"}\n" +
            $"主题: {entry.Subject ?? "无"}\n" +
            $"人物ID: {entry.PersonId?.ToString() ?? "无"}\n" +
            $"频道ID: {entry.ChannelId?.ToString() ?? "无"}\n" +
            $"重要性: {entry.Importance:F2}\n" +
            $"确定性: {entry.Certainty:F2}\n" +
            $"持久化: {entry.IsPersistent}\n" +
            $"创建时间: {entry.CreatedAt:O}\n" +
            $"过期时间: {expires}");
    }

    private static ToolResult Ok(string data) => new() { Status = "success", Data = data };
    private static ToolResult Fail(string err) => new() { Status = "failed", Error = err };
}
