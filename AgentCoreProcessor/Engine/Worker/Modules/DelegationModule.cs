using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace AgentCoreProcessor.Engine.Modules
{
    /// <summary>
    /// 委托状态模块。频道循环专用。
    /// 每轮检查本频道的委托状态（进行中/已完成），注入 prompt 让模型感知。
    /// </summary>
    internal class DelegationModule : EngineModule
    {
        public override string Name => "委托状态";
        public override int PromptPriority => 42;

        private readonly DelegationRegistry registry;
        private readonly int channelId;

        public DelegationModule(DelegationRegistry registry, int channelId)
        {
            this.registry = registry;
            this.channelId = channelId;
        }

        public override void Attach(ILoopBus bus) { }

        public override string? BuildPromptSection(EngineMode mode)
        {
            if (mode != EngineMode.Working) return null;

            var completed = registry.GetCompletedForChannel(channelId);
            var active = registry.GetActiveForChannel(channelId);

            if (completed.Count == 0 && active.Count == 0)
                return null;

            var sb = new StringBuilder("[委托状态]\n");

            if (completed.Count > 0)
            {
                foreach (var d in completed)
                {
                    var statusLabel = d.Status == DelegationStatus.Completed ? "已完成" : "失败";
                    sb.AppendLine($"- 委托#{d.DelegationId} ({d.Description}): {statusLabel}");
                    sb.AppendLine($"  结果: {d.Result}");
                    registry.ConsumeCompleted(d.DelegationId);
                }
            }

            if (active.Count > 0)
            {
                foreach (var d in active)
                {
                    var statusLabel = d.Status switch
                    {
                        DelegationStatus.Accepted => "已接受，等待执行",
                        DelegationStatus.Queued => "排队中",
                        DelegationStatus.Executing => "执行中",
                        _ => d.Status.ToString()
                    };
                    sb.AppendLine($"- 委托#{d.DelegationId} ({d.Description}): {statusLabel}");
                }
            }

            return sb.ToString();
        }
    }
}
