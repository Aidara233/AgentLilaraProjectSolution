using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.MemoryTools;

[ToolMeta(ContinueLoop = true, CapabilitySummary = "记忆更新：修改已有记忆的内容")]
public class MemoryUpdateTool : ITool
{
    private readonly IMemoryAccess? memory;

    public string Name => "memory_update";
    public string Description => "更新主记忆库中一条记忆的内容（自动重新计算 embedding）。";
    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("id", "记忆 ID", 0),
        new("content", "新内容", 1)
    ];
    public TimeSpan Timeout => TimeSpan.FromSeconds(30);

    public MemoryUpdateTool(IMemoryAccess? memoryAccess)
    {
        memory = memoryAccess;
    }

    public async Task<ToolResult> ExecuteAsync(List<string> inputs, CancellationToken ct)
    {
        if (memory == null)
            return Fail("记忆服务不可用");

        if (!int.TryParse(inputs[0], out var id))
            return Fail("id 必须为整数");

        if (inputs.Count < 2 || string.IsNullOrWhiteSpace(inputs[1]))
            return Fail("content 不能为空");

        await memory.UpdateAsync(id, inputs[1].Trim());
        return Ok($"已更新记忆 id={id}");
    }

    private static ToolResult Ok(string data) => new() { Status = "success", Data = data };
    private static ToolResult Fail(string err) => new() { Status = "failed", Error = err };
}
