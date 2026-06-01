using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.MemoryTools;

[ToolMeta(ContinueLoop = true, CapabilitySummary = "记忆关联创建：在两个记忆之间建立关联")]
public class MemoryLinkCreateTool : ITool
{
    private readonly IMemoryAccess? memory;

    public string Name => "memory_link_create";
    public string Description => "在两个记忆之间建立关联。关联类型：cooccurrence（共现）、temporal（时序）、semantic（语义）、causal（因果）。";
    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("from_id", "源记忆 ID", 0),
        new("to_id", "目标记忆 ID", 1),
        new("relevance", "（可选）关联性 0.0-1.0，默认 1.0", 2, false),
        new("link_type", "（可选）关联类型：cooccurrence / temporal / semantic / causal，默认 semantic", 3, false)
    ];
    public TimeSpan Timeout => TimeSpan.FromSeconds(10);

    public MemoryLinkCreateTool(IMemoryAccess? memoryAccess)
    {
        memory = memoryAccess;
    }

    public async Task<ToolResult> ExecuteAsync(List<string> inputs, CancellationToken ct)
    {
        if (memory == null)
            return Fail("记忆服务不可用");

        if (!int.TryParse(inputs[0], out var fromId) || !int.TryParse(inputs[1], out var toId))
            return Fail("from_id 和 to_id 必须为整数");

        var relevance = inputs.Count > 2 && float.TryParse(inputs[2], out var s) ? s : 1.0f;
        var linkType = inputs.Count > 3 && !string.IsNullOrWhiteSpace(inputs[3]) ? inputs[3].Trim().ToLower() : "semantic";

        var validTypes = new[] { "cooccurrence", "temporal", "semantic", "causal" };
        if (Array.IndexOf(validTypes, linkType) < 0)
            return Fail($"link_type 必须为 cooccurrence / temporal / semantic / causal，得到: {linkType}");

        await memory.LinkAsync(fromId, toId, relevance, 1.0f, linkType);
        return Ok($"已创建关联: {fromId} ↔ {toId} (relevance={relevance:F2}, type={linkType})");
    }

    private static ToolResult Ok(string data) => new() { Status = "success", Data = data };
    private static ToolResult Fail(string err) => new() { Status = "failed", Error = err };
}
