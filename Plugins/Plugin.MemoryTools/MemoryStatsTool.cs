using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.MemoryTools;

[ToolMeta(ContinueLoop = true, CapabilitySummary = "记忆统计：查看记忆库容量")]
public class MemoryStatsTool : ITool
{
    private readonly IMemoryAccess? memory;

    public string Name => "memory_stats";
    public string Description => "查看记忆库统计信息：主记忆库和临时记忆库的条目数量。";
    public IReadOnlyList<ToolParameter> Parameters => [];

    public TimeSpan Timeout => TimeSpan.FromSeconds(5);

    public MemoryStatsTool(IMemoryAccess? memoryAccess)
    {
        memory = memoryAccess;
    }

    public async Task<ToolResult> ExecuteAsync(List<string> inputs, CancellationToken ct)
    {
        if (memory == null)
            return Fail("记忆服务不可用");

        var mainTask = memory.CountAsync();
        var tempTask = memory.CountTempAsync();
        await Task.WhenAll(mainTask, tempTask);

        return Ok($"主记忆库: {mainTask.Result} 条\n临时记忆库: {tempTask.Result} 条\n合计: {mainTask.Result + tempTask.Result} 条");
    }

    private static ToolResult Ok(string data) => new() { Status = "success", Data = data };
    private static ToolResult Fail(string err) => new() { Status = "failed", Error = err };
}
