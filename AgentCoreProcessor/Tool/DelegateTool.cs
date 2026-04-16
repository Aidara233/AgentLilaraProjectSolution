using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AgentCoreProcessor.Tool
{
    /// <summary>
    /// 委派任务工具：将复杂/耗时任务委派给子 agent 执行。
    /// 信号工具——返回分配的子 agent ID，实际启动由 WorkingCore 副作用处理。
    /// </summary>
    internal class DelegateTool : ITool
    {
        public string Name => "委派任务";
        public string Description => "将一个任务委派给子agent异步执行。适合需要多步探索或耗时的任务。简单明确的操作直接用其他工具完成即可";
        public IReadOnlyList<ToolParameter> Parameters =>
            [new("任务描述", "用自然语言描述要完成的任务", 0)];
        public TimeSpan Timeout => TimeSpan.FromSeconds(1);
        public bool AllowSubAgent => false;

        public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            if (resolvedInputs.Count < 1 || string.IsNullOrWhiteSpace(resolvedInputs[0]))
                return Task.FromResult(new ToolResult { Status = "failed", Error = "任务描述不能为空" });

            return Task.FromResult(new ToolResult
            {
                Status = "success",
                Data = resolvedInputs[0]
            });
        }
    }

    /// <summary>
    /// 查看子任务详情工具：查看子 agent 的执行日志和状态。
    /// 信号工具——返回子 agent ID，实际查询由 WorkingCore 副作用处理。
    /// </summary>
    internal class SubAgentDetailTool : ITool
    {
        public string Name => "查看子任务详情";
        public string Description => "查看指定子agent的执行日志和详细状态，用于调试子任务失败原因";
        public IReadOnlyList<ToolParameter> Parameters =>
            [new("子任务ID", "子agent的ID，如 sa_01", 0)];
        public TimeSpan Timeout => TimeSpan.FromSeconds(1);
        public bool AllowSubAgent => false;

        public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            if (resolvedInputs.Count < 1 || string.IsNullOrWhiteSpace(resolvedInputs[0]))
                return Task.FromResult(new ToolResult { Status = "failed", Error = "子任务ID不能为空" });

            return Task.FromResult(new ToolResult
            {
                Status = "success",
                Data = resolvedInputs[0]
            });
        }
    }
}
