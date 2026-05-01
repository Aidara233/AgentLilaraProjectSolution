using System;
using System.Linq;
using System.Text;

namespace AgentCoreProcessor.Engine.Modules
{
    /// <summary>
    /// 系统状态模块。注入活跃子 agent、活跃频道、通知数、时间、自身状态。
    /// </summary>
    internal class SystemStatusModule : EngineModule
    {
        public override string Name => "系统状态";
        public override int PromptPriority => 35; // 最高优先级

        private readonly ISystemContext ctx;
        private readonly Func<List<IAgentSession>>? getSubAgents;

        public SystemStatusModule(ISystemContext ctx, Func<List<IAgentSession>>? getSubAgents = null)
        {
            this.ctx = ctx;
            this.getSubAgents = getSubAgents;
        }

        public override void Attach(ILoopBus bus)
        {
            // 不需要订阅事件，每轮直接读取系统状态
        }

        public override string? BuildPromptSection(EngineMode mode)
        {
            if (mode != EngineMode.Working) return null;

            var sb = new StringBuilder("[系统状态]\n");

            // 当前时间
            sb.AppendLine($"当前时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

            // 活跃引擎
            var engines = ctx.GetActiveEngineSummary();
            if (engines.Any())
            {
                sb.AppendLine($"活跃引擎: {string.Join(", ", engines.Select(e => $"{e.Type}({e.Count})"))}");
            }
            else
            {
                sb.AppendLine("活跃引擎: 无");
            }

            // 系统空闲状态
            sb.AppendLine($"系统空闲: {(ctx.IsIdle ? "是" : "否")}");
            if (ctx.IsIdle)
            {
                sb.AppendLine($"空闲时长: {ctx.IdleDuration.TotalMinutes:F1} 分钟");
            }

            // 活跃子 agent
            if (getSubAgents != null)
            {
                var subAgents = getSubAgents();
                if (subAgents.Count > 0)
                {
                    sb.AppendLine($"活跃子 agent: {subAgents.Count} 个");
                    foreach (var agent in subAgents.Take(5)) // 最多显示 5 个
                    {
                        sb.AppendLine($"  - {agent.SessionId} ({agent.Type})");
                    }
                    if (subAgents.Count > 5)
                    {
                        sb.AppendLine($"  ... 还有 {subAgents.Count - 5} 个");
                    }
                }
                else
                {
                    sb.AppendLine("活跃子 agent: 无");
                }
            }
            else
            {
                sb.AppendLine("活跃子 agent: 无");
            }

            // 通知数量（Phase 2 简化：暂不实现按需拉取）
            sb.AppendLine("新通知: 0（使用 CheckNotificationsTool 查询）");

            return sb.ToString();
        }
    }
}
