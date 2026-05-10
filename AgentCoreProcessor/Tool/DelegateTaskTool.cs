using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Engine;

namespace AgentCoreProcessor.Tool
{
    /// <summary>
    /// 委派任务工具：将任务提交给系统循环评估和处理。
    /// 频道循环专用。同步等待系统循环评估（accept/queue/reject），异步等待执行结果。
    /// </summary>
    internal class DelegateTaskTool : ITool
    {
        public string Name => "委派任务";
        public string Description => "将复杂任务提交给系统循环评估和处理。会同步等待评估结果（接受/排队/拒绝），执行结果稍后自动送达。";
        public IReadOnlyList<ToolParameter> Parameters =>
        [
            new("任务描述", "详细描述需要完成的任务", 0),
            new("上下文摘要", "可选：当前对话的关键上下文（帮助系统循环理解背景）", 1)
        ];
        public TimeSpan Timeout => TimeSpan.FromSeconds(20);
        public bool AllowSubAgent => false;
        public bool ContinueLoop => true;
        public string? CapabilitySummary => "将复杂任务委派给系统循环";

        private static readonly TimeSpan EvaluationTimeout = TimeSpan.FromSeconds(15);

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

            var delegation = new Delegation
            {
                SourceChannelId = currentChannelId,
                Description = description,
                ContextSummary = contextSummary,
                RequestingPersonId = currentPersonId
            };

            // 提交委托（会自动唤醒系统循环）
            ctx.Delegations.Submit(delegation);

            // 同步等待系统循环评估
            var evaluation = await ctx.Delegations.WaitForEvaluationAsync(
                delegation.DelegationId, EvaluationTimeout);

            if (evaluation == null)
            {
                return new ToolResult
                {
                    Status = "timeout",
                    Data = $"[超时] 委托 {delegation.DelegationId} 评估超时，稍后再看结果。"
                };
            }

            var verdictText = evaluation.Verdict switch
            {
                DelegationStatus.Accepted => "已接受",
                DelegationStatus.Queued => "已排队",
                DelegationStatus.Rejected => "已拒绝",
                _ => evaluation.Verdict.ToString()
            };

            return new ToolResult
            {
                Status = "success",
                Data = $"[{verdictText}] {evaluation.Reason}\n委托ID: {delegation.DelegationId}"
            };
        }
    }
}
