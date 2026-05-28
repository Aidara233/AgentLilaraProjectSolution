using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.MemoryTools;

[ToolMeta(ContinueLoop = true, CapabilitySummary = "记忆删除：按 ID 删除主记忆或临时记忆")]
public class MemoryDeleteTool : ITool
{
    private readonly IMemoryAccess? memory;

    public string Name => "memory_delete";
    public string Description => "删除一条记忆。target=main 删除主记忆，target=temp 删除临时记忆。";
    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("id", "记忆 ID", 0),
        new("target", "目标库：main 或 temp", 1)
    ];
    public TimeSpan Timeout => TimeSpan.FromSeconds(10);

    public MemoryDeleteTool(IMemoryAccess? memoryAccess)
    {
        memory = memoryAccess;
    }

    public async Task<ToolResult> ExecuteAsync(List<string> inputs, CancellationToken ct)
    {
        if (memory == null)
            return Fail("记忆服务不可用");

        if (!int.TryParse(inputs[0], out var id))
            return Fail("id 必须为整数");

        var target = inputs.Count > 1 ? inputs[1].Trim().ToLower() : "main";
        if (target == "temp")
        {
            await memory.DeleteTempAsync(id);
            return Ok($"已从临时记忆库删除 id={id}");
        }

        if (target != "main")
            return Fail("target 必须为 main 或 temp");

        await memory.DeleteAsync(id);
        return Ok($"已从主记忆库删除 id={id}");
    }

    private static ToolResult Ok(string data) => new() { Status = "success", Data = data };
    private static ToolResult Fail(string err) => new() { Status = "failed", Error = err };
}
