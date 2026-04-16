using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AgentCoreProcessor.Tool
{
    /// <summary>
    /// 标记复盘工具。WorkerEngine 工作时记录"值得复盘关注"的内容。
    /// 信号工具——返回内容作为 Data，由 Agent 循环处理实际写入。
    /// </summary>
    internal class ReviewHintTool : ITool
    {
        public string Name => "标记复盘";
        public string Description => "标记一条内容供睡眠复盘时重点关注（由框架自动关联当前用户、频道、话题）";
        public IReadOnlyList<ToolParameter> Parameters =>
            [new("标记内容", "值得复盘时深入分析的内容", 0)];
        public TimeSpan Timeout => TimeSpan.FromSeconds(1);
        public bool AllowSubAgent => false;

        public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            if (resolvedInputs.Count < 1 || string.IsNullOrWhiteSpace(resolvedInputs[0]))
                return Task.FromResult(new ToolResult { Status = "failed", Error = "标记内容不能为空" });

            return Task.FromResult(new ToolResult { Status = "success", Data = resolvedInputs[0] });
        }
    }
}
