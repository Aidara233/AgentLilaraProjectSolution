using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Engine;

namespace AgentCoreProcessor.Tool
{
    /// <summary>
    /// 委派任务工具：将任务提交给系统循环处理（异步，不阻塞）。
    /// 频道循环专用，用于处理复杂任务（文件写入、远程命令、跨频道协调等）。
    /// 系统循环处理完成后会通过 SendToChannel 回传结果。
    /// </summary>
    internal class DelegateTaskTool : ITool
    {
        public string Name => "委派任务";
        public string Description => "将复杂任务提交给系统循环处理（异步，系统循环完成后会回传结果到当前频道）";
        public IReadOnlyList<ToolParameter> Parameters =>
        [
            new("任务描述", "详细描述需要完成的任务", 0),
            new("上下文摘要", "可选：当前对话的关键上下文（帮助系统循环理解背景）", 1)
        ];
        public TimeSpan Timeout => TimeSpan.FromSeconds(10);
        public bool AllowSubAgent => false;
        public bool ContinueLoop => true;
        public string? CapabilitySummary => "将复杂任务委派给系统循环";

        private readonly ISystemContext ctx;
        private int currentChannelId;
        private int currentPersonId;

        public DelegateTaskTool(ISystemContext ctx)
        {
            this.ctx = ctx;
        }

        public void SetContext(int channelId, int personId)
        {
            currentChannelId = channelId;
            currentPersonId = personId;
        }

        public async Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            if (resolvedInputs.Count < 1 || string.IsNullOrWhiteSpace(resolvedInputs[0]))
            {
                return new ToolResult
                {
                    Status = "failed",
                    Error = "任务描述不能为空"
                };
            }

            var description = resolvedInputs[0];
            var contextSummary = resolvedInputs.Count > 1 ? resolvedInputs[1] : null;

            var task = new SystemTask
            {
                TaskId = Guid.NewGuid().ToString(),
                SourceChannelId = currentChannelId,
                Description = description,
                ContextSummary = contextSummary,
                RequestingPersonId = currentPersonId,
                Priority = 50,
                SubmittedAt = DateTime.Now
            };

            // 异步提交（不等待结果）
            await ctx.TaskBridge.SubmitTaskFireAndForgetAsync(task);

            return new ToolResult
            {
                Status = "success",
                Data = $"任务已异步提交给系统循环。任务ID: {task.TaskId}\n系统循环处理完成后会将结果发送到当前频道。"
            };
        }
    }
}
