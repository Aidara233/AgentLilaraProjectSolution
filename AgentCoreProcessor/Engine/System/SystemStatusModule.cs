using System;
using System.Linq;
using System.Text;

namespace AgentCoreProcessor.Engine.Modules
{
    /// <summary>
    /// 系统状态模块。注入仪表盘：时间、运行时长、上下文使用率、频道状态、子agent、定时任务。
    /// </summary>
    internal class SystemStatusModule : EngineModule
    {
        public override string Name => "系统状态";
        public override int PromptPriority => 35;

        private readonly ISystemContext ctx;
        private readonly Func<System.Collections.Generic.List<IAgentSession>>? getSubAgents;
        private readonly Func<(int tokens, int percent)>? getContextUsage;
        private DateTime? engineStartTime;

        public SystemStatusModule(
            ISystemContext ctx,
            Func<System.Collections.Generic.List<IAgentSession>>? getSubAgents = null,
            Func<(int tokens, int percent)>? getContextUsage = null)
        {
            this.ctx = ctx;
            this.getSubAgents = getSubAgents;
            this.getContextUsage = getContextUsage;
        }

        public override void Attach(ILoopBus bus)
        {
            engineStartTime = DateTime.Now;
        }

        public override string? BuildPromptSection(EngineMode mode)
        {
            if (mode != EngineMode.Working) return null;

            var sb = new StringBuilder("[系统状态]\n");

            // 时间 + 运行时长
            var now = DateTime.Now;
            var uptime = engineStartTime.HasValue ? now - engineStartTime.Value : TimeSpan.Zero;
            sb.AppendLine($"时间: {now:yyyy-MM-dd HH:mm:ss} | 运行: {FormatUptime(uptime)}");

            // 上下文使用率
            if (getContextUsage != null)
            {
                var (tokens, percent) = getContextUsage();
                var mood = percent switch
                {
                    < 40 => "清醒",
                    < 60 => "舒适",
                    < 75 => "有点困了",
                    < 85 => "很困",
                    _ => "快撑不住了"
                };
                sb.AppendLine($"上下文: {tokens / 1000}k/80k tokens ({percent}%) — {mood}");
            }

            // 频道状态（简要）
            try
            {
                var channels = ctx.Session.GetAllChannelsAsync().GetAwaiter().GetResult();
                if (channels.Count > 0)
                {
                    sb.AppendLine($"频道: {channels.Count}个已注册");
                    foreach (var ch in channels.Take(6))
                    {
                        var displayName = ch.Name.Contains(':') ? ch.Name.Split(':', 2)[1] : ch.Name;
                        sb.AppendLine($"  - {displayName} (id:{ch.Id})");
                    }
                    if (channels.Count > 6)
                        sb.AppendLine($"  ... 还有 {channels.Count - 6} 个");
                }
            }
            catch
            {
                sb.AppendLine("频道: (读取失败)");
            }

            // 子 agent
            if (getSubAgents != null)
            {
                var subAgents = getSubAgents();
                if (subAgents.Count > 0)
                {
                    sb.AppendLine($"子agent: {subAgents.Count}个运行中");
                    foreach (var agent in subAgents.Take(5))
                        sb.AppendLine($"  - {agent.SessionId} ({agent.Type})");
                }
                else
                {
                    sb.AppendLine("子agent: 无");
                }
            }

            // 系统空闲
            sb.AppendLine($"系统空闲: {(ctx.IsIdle ? $"是 ({ctx.IdleDuration.TotalMinutes:F0}min)" : "否")}");

            return sb.ToString();
        }

        private static string FormatUptime(TimeSpan ts)
        {
            if (ts.TotalHours >= 1)
                return $"{(int)ts.TotalHours}h {ts.Minutes}m";
            return $"{ts.Minutes}m";
        }

        private static string FormatTimeAgo(TimeSpan ts)
        {
            if (ts.TotalSeconds < 60) return $"{(int)ts.TotalSeconds}s前";
            if (ts.TotalMinutes < 60) return $"{(int)ts.TotalMinutes}min前";
            if (ts.TotalHours < 24) return $"{(int)ts.TotalHours}h前";
            return $"{(int)ts.TotalDays}d前";
        }
    }
}
