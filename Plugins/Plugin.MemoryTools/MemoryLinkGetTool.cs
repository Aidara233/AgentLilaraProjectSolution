using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.MemoryTools;

[ToolMeta(ContinueLoop = true, CapabilitySummary = "记忆关联查询：查看与某条记忆关联的所有记忆")]
public class MemoryLinkGetTool : ITool
{
    private readonly IMemoryAccess? memory;

    public string Name => "memory_link_get";
    public string Description => "查询与指定记忆关联的所有记忆，包含关联性、类型、建立时间等元数据。";
    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("memory_id", "记忆 ID", 0)
    ];
    public TimeSpan Timeout => TimeSpan.FromSeconds(10);

    public MemoryLinkGetTool(IMemoryAccess? memoryAccess)
    {
        memory = memoryAccess;
    }

    public async Task<ToolResult> ExecuteAsync(List<string> inputs, CancellationToken ct)
    {
        if (memory == null)
            return Fail("记忆服务不可用");

        if (!int.TryParse(inputs[0], out var memoryId))
            return Fail("memory_id 必须为整数");

        var links = await memory.GetLinkedAsync(memoryId);
        if (links.Count == 0)
            return Ok($"记忆 ID:{memoryId} 没有关联");

        var sb = new StringBuilder();
        sb.AppendLine($"记忆 ID:{memoryId} 的关联 ({links.Count} 条):");
        foreach (var l in links)
        {
            sb.AppendLine($"[{l.MemoryId}] (relevance={l.Relevance:F2}, type={l.LinkType}, linked={l.LinkedAt:yyyy-MM-dd HH:mm}) {l.Content}");
            if (l.Subject != null) sb.AppendLine($"    subject: {l.Subject}");
        }
        return Ok(sb.ToString().TrimEnd());
    }

    private static ToolResult Ok(string data) => new() { Status = "success", Data = data };
    private static ToolResult Fail(string err) => new() { Status = "failed", Error = err };
}
