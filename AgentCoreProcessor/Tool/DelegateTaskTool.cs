using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Engine;

namespace AgentCoreProcessor.Tool
{
    /// <summary>
    /// 委派任务工具：将任务提交给系统循环处理。
    /// 频道循环专用，用于处理复杂任务（文件写入、远程命令、跨频道协调等）。
    /// </summary>
    internal class DelegateTaskTool : ITool
    {
        public string Name => "委派任务";
        public string Description => "将复杂任务提交给系统循环处理（文件写入、远程命令、跨频道协调等）";
        public IReadOnlyList<ToolParameter> Parameters =>
        [
            new("任务描述", "详细描述需要完成的任务", 0),
            new("上下文摘要", "可选：当前对话的关键上下文（帮助系统循环理解背景）", 1)
        ];
        public TimeSpan Timeout => TimeSpan.FromSeconds(120);
        public bool AllowSubAgent => false;
        public string? CapabilitySummary => "将复杂任务委派给系统循环";

        private readonly ISystemContext ctx;
        private int currentChannelId;
        private int currentPersonId;

        public DelegateTaskTool(ISystemContext ctx)
        {
            this.ctx = ctx;
        }

        /// <summary>
        /// 设置当前上下文（由 ChannelEngine 在每轮开始时调用）。
        /// </summary>
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

            // 构造 SystemTask
            var task = new SystemTask
            {
                TaskId = Guid.NewGuid().ToString(),
                SourceChannelId = currentChannelId,
                Description = description,
                ContextSummary = contextSummary,
                RequestingPersonId = currentPersonId,
                Priority = 50, // 默认优先级
                SubmittedAt = DateTime.Now
            };

            try
            {
                // 提交任务（非阻塞，120s 超时）
                var result = await ctx.TaskBridge.SubmitTaskAsync(task, TimeSpan.FromSeconds(120));

                if (result.Success)
                {
                    return new ToolResult
                    {
                        Status = "success",
                        Data = $"任务已提交给系统循环。\n任务 ID: {task.TaskId}\n结果: {result.Result}"
                    };
                }
                else
                {
                    return new ToolResult
                    {
                        Status = "failed",
                        Error = $"任务处理失败: {result.Error}"
                    };
                }
            }
            catch (Exception ex)
            {
                return new ToolResult
                {
                    Status = "failed",
                    Error = $"提交任务时发生异常: {ex.Message}"
                };
            }
        }
    }
}
