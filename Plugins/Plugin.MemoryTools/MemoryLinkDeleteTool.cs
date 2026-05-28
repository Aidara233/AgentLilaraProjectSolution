using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.MemoryTools;

[ToolMeta(ContinueLoop = true, CapabilitySummary = "记忆关联删除：删除两条记忆之间的关联")]
public class MemoryLinkDeleteTool : ITool
{
    private readonly IMemoryAccess? memory;

    public string Name => "memory_link_delete";
    public string Description => "删除两个记忆之间的关联。关联是无向的，from_id 和 to_id 顺序无关。";
    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("from_id", "记忆 A 的 ID", 0),
        new("to_id", "记忆 B 的 ID", 1)
    ];
    public TimeSpan Timeout => TimeSpan.FromSeconds(10);

    public MemoryLinkDeleteTool(IMemoryAccess? memoryAccess)
    {
        memory = memoryAccess;
    }

    public async Task<ToolResult> ExecuteAsync(List<string> inputs, CancellationToken ct)
    {
        if (memory == null)
            return Fail("记忆服务不可用");

        if (!int.TryParse(inputs[0], out var fromId) || !int.TryParse(inputs[1], out var toId))
            return Fail("from_id 和 to_id 必须为整数");

        await memory.UnlinkAsync(fromId, toId);
        return Ok($"已删除关联: {fromId} ↔ {toId}");
    }

    private static ToolResult Ok(string data) => new() { Status = "success", Data = data };
    private static ToolResult Fail(string err) => new() { Status = "failed", Error = err };
}
