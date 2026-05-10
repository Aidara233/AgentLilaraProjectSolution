using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Engine;

namespace AgentCoreProcessor.Tool
{
    /// <summary>
    /// 评估委托工具。系统循环专用。
    /// 对频道循环提交的委托进行评估（接受/排队/拒绝），评估结果同步返回给等待中的频道循环。
    /// </summary>
    internal class EvaluateDelegationTool : ITool
    {
        public string Name => "评估委托";
        public string Description => "评估频道循环提交的委托（频道循环正在等待你的评估结果）";
        public IReadOnlyList<ToolParameter> Parameters =>
        [
            new("委托ID", "待评估委托的 ID", 0),
            new("决定", "accept（接受并执行）/ queue（排队稍后执行）/ reject（拒绝）", 1),
            new("理由", "评估理由（会传达给频道循环）", 2)
        ];
        public TimeSpan Timeout => TimeSpan.FromSeconds(5);
        public bool ContinueLoop => true;
        public bool AllowSubAgent => false;

        private readonly DelegationRegistry registry;
        private readonly Func<string, string?, IAgentSession> subAgentFactory;

        public EvaluateDelegationTool(DelegationRegistry registry, Func<string, string?, IAgentSession> subAgentFactory)
        {
            this.registry = registry;
            this.subAgentFactory = subAgentFactory;
        }

        public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            if (resolvedInputs.Count < 3
                || string.IsNullOrWhiteSpace(resolvedInputs[0])
                || string.IsNullOrWhiteSpace(resolvedInputs[1])
                || string.IsNullOrWhiteSpace(resolvedInputs[2]))
            {
                return Task.FromResult(new ToolResult
                {
                    Status = "failed",
                    Error = "需要三个参数：委托ID、决定(accept/queue/reject)、理由"
                });
            }

            var delegationId = resolvedInputs[0];
            var decision = resolvedInputs[1].Trim().ToLower();
            var reason = resolvedInputs[2];

            var verdict = decision switch
            {
                "accept" => DelegationStatus.Accepted,
                "queue" => DelegationStatus.Queued,
                "reject" => DelegationStatus.Rejected,
                _ => (DelegationStatus?)null
            };

            if (verdict == null)
            {
                return Task.FromResult(new ToolResult
                {
                    Status = "failed",
                    Error = "决定必须是 accept、queue 或 reject"
                });
            }

            var evaluation = new DelegationEvaluation
            {
                Verdict = verdict.Value,
                Reason = reason
            };

            var success = registry.ResolveEvaluation(delegationId, evaluation);
            if (!success)
            {
                return Task.FromResult(new ToolResult
                {
                    Status = "failed",
                    Error = $"委托 {delegationId} 不存在"
                });
            }

            // 如果接受，立即创建子 agent 执行
            if (verdict == DelegationStatus.Accepted)
            {
                var delegation = registry.Get(delegationId);
                if (delegation != null)
                {
                    var instruction = delegation.Description;
                    if (!string.IsNullOrEmpty(delegation.ContextSummary))
                        instruction += $"\n\n上下文: {delegation.ContextSummary}";

                    registry.MarkExecuting(delegationId);
                    subAgentFactory(instruction, delegationId);
                }
            }

            return Task.FromResult(new ToolResult
            {
                Status = "success",
                Data = $"委托 {delegationId} 已评估为 {decision}，理由已传达给频道循环"
            });
        }
    }
}
