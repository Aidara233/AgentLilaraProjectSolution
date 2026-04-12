using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AgentCoreProcessor.Tool
{
    /// <summary>
    /// 记忆工具：将一条信息记录到临时记忆库。
    /// 纯信号工具——返回记忆内容，由 Agent 循环调用 MemoryService.StoreAsync 写入。
    /// </summary>
    internal class MemoryTool : ITool
    {
        public string Name => "记忆";
        public string Description => "记录一条信息到记忆库（由框架自动关联当前用户、频道、话题）";
        public IReadOnlyList<ToolParameter> Parameters =>
            [new("记忆内容", "要记住的信息", 0)];
        public TimeSpan Timeout => TimeSpan.FromSeconds(1);

        public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            if (resolvedInputs.Count < 1 || string.IsNullOrWhiteSpace(resolvedInputs[0]))
                return Task.FromResult(new ToolResult
                {
                    Status = "failed",
                    Error = "记忆内容不能为空"
                });

            return Task.FromResult(new ToolResult
            {
                Status = "success",
                Data = resolvedInputs[0]
            });
        }
    }
}
