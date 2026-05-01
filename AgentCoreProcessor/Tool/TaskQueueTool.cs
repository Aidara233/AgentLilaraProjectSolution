using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AgentCoreProcessor.Tool
{
    /// <summary>
    /// 任务队列工具：操作任务队列（跳过/延后/放弃）。
    /// 系统循环专用。
    /// </summary>
    internal class TaskQueueTool : ITool
    {
        public string Name => "任务队列";
        public string Description => "操作任务队列（skip 跳过当前任务 / postpone 延后 / abandon 放弃）";
        public IReadOnlyList<ToolParameter> Parameters =>
        [
            new("操作", "skip（跳过当前任务）/ postpone（延后）/ abandon（放弃）", 0),
            new("任务ID", "可选：指定任务 ID（不指定则操作当前任务）", 1)
        ];
        public TimeSpan Timeout => TimeSpan.FromSeconds(1);
        public bool AllowSubAgent => false;

        public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            if (resolvedInputs.Count < 1 || string.IsNullOrWhiteSpace(resolvedInputs[0]))
            {
                return Task.FromResult(new ToolResult
                {
                    Status = "failed",
                    Error = "操作不能为空"
                });
            }

            var action = resolvedInputs[0].ToLower().Trim();
            var taskId = resolvedInputs.Count > 1 ? resolvedInputs[1] : null;
            var validActions = new[] { "skip", "postpone", "abandon" };

            if (!validActions.Contains(action))
            {
                return Task.FromResult(new ToolResult
                {
                    Status = "failed",
                    Error = $"无效的操作：{action}。有效值：skip / postpone / abandon"
                });
            }

            // Phase 4: 返回操作信号，实际队列操作在 Phase 5+ 实现
            var data = string.IsNullOrEmpty(taskId)
                ? $"queue:{action}"
                : $"queue:{action}:{taskId}";

            return Task.FromResult(new ToolResult
            {
                Status = "success",
                Data = data
            });
        }
    }
}
