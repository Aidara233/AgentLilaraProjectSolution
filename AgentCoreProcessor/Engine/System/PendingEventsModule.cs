using System.Collections.Generic;
using System.Linq;
using System.Text;
using AgentCoreProcessor.Engine;

namespace AgentCoreProcessor.Engine.Modules
{
    /// <summary>
    /// 待处理事件模块。替代 TaskQueueModule。
    /// 每轮收集所有待处理事件（任务、通知、定时任务到期、待评估委托），格式化注入 prompt。
    /// </summary>
    internal class PendingEventsModule : EngineModule
    {
        public override string Name => "待处理事件";
        public override int PromptPriority => 38;

        private readonly List<SystemTask> pendingTasks = new();
        private readonly List<Notification> pendingNotifications = new();
        private readonly List<ScheduledTaskFiredEvent> firedScheduledTasks = new();
        private readonly List<Delegation> pendingDelegations = new();
        private readonly List<Delegation> retryPendingDelegations = new();
        private bool noActionLastRound;

        /// <summary>清空并重新填充本轮待处理事件。由 SystemEngine 在每轮开始时调用。</summary>
        public void SetPendingEvents(
            List<SystemTask> tasks,
            List<Notification> notifications,
            List<ScheduledTaskFiredEvent> scheduledTasks,
            bool hadNoAction)
        {
            pendingTasks.Clear();
            pendingTasks.AddRange(tasks);
            pendingNotifications.Clear();
            pendingNotifications.AddRange(notifications);
            firedScheduledTasks.Clear();
            firedScheduledTasks.AddRange(scheduledTasks);
            noActionLastRound = hadNoAction;
        }

        /// <summary>设置待评估委托列表。</summary>
        public void SetPendingDelegations(List<Delegation> delegations)
        {
            pendingDelegations.Clear();
            pendingDelegations.AddRange(delegations);
        }

        /// <summary>设置等待重试决策的委托列表。</summary>
        public void SetRetryPendingDelegations(List<Delegation> delegations)
        {
            retryPendingDelegations.Clear();
            retryPendingDelegations.AddRange(delegations);
        }

        public override void Attach(ILoopBus bus) { }

        public override string? BuildPromptSection(EngineMode mode)
        {
            if (mode != EngineMode.Working) return null;

            var sb = new StringBuilder("[待处理事件]\n");

            if (noActionLastRound)
            {
                sb.AppendLine("⚠ 你上一轮没有执行任何操作。请明确下一步行动，或调用「等待」工具进入等待状态。");
                sb.AppendLine();
            }

            bool hasAny = pendingTasks.Count > 0 || pendingNotifications.Count > 0
                || firedScheduledTasks.Count > 0 || pendingDelegations.Count > 0
                || retryPendingDelegations.Count > 0;

            if (!hasAny)
            {
                sb.AppendLine("无新事件。你可以主动检查系统状态，或调用「等待」工具进入等待。");
                return sb.ToString();
            }

            // 委托优先（频道循环在等待）
            if (pendingDelegations.Count > 0)
            {
                sb.AppendLine($"--- ⚡ 待评估委托 ({pendingDelegations.Count}) [紧急：频道循环正在等待] ---");
                foreach (var d in pendingDelegations)
                {
                    sb.AppendLine($"  委托#{d.DelegationId}: {d.Description}");
                    sb.AppendLine($"    来源频道: {d.SourceChannelId}, 请求者: Person#{d.RequestingPersonId}");
                    if (!string.IsNullOrEmpty(d.ContextSummary))
                        sb.AppendLine($"    上下文: {d.ContextSummary}");
                }
                sb.AppendLine("  → 请立即对每个委托调用「评估委托」工具（accept/queue/reject）");
                sb.AppendLine();
            }

            if (retryPendingDelegations.Count > 0)
            {
                sb.AppendLine($"--- ⚠ 执行失败待重试 ({retryPendingDelegations.Count}) ---");
                foreach (var d in retryPendingDelegations)
                {
                    sb.AppendLine($"  委托#{d.DelegationId}: {d.Description}");
                    sb.AppendLine($"    失败原因: {d.Result?.Truncate(80)}");
                    sb.AppendLine($"    已重试: {d.RetryCount}/{DelegationRegistry.MaxRetries}");
                }
                sb.AppendLine("  → 如果还有重试余量，可重新创建子 agent 执行；否则调用「标记委托失败」放弃。");
                sb.AppendLine();
            }

            if (pendingTasks.Count > 0)
            {
                sb.AppendLine($"--- 新任务 ({pendingTasks.Count}) ---");
                foreach (var task in pendingTasks)
                {
                    sb.AppendLine($"  任务#{task.TaskId}: {task.Description}");
                    sb.AppendLine($"    来源频道: {task.SourceChannelId}, 请求者: Person#{task.RequestingPersonId}, 优先级: {task.Priority}");
                    if (!string.IsNullOrEmpty(task.ContextSummary))
                        sb.AppendLine($"    上下文: {task.ContextSummary}");
                }
                sb.AppendLine();
            }

            if (pendingNotifications.Count > 0)
            {
                sb.AppendLine($"--- 通知 ({pendingNotifications.Count}) ---");
                foreach (var n in pendingNotifications)
                {
                    sb.AppendLine($"  [{n.Type}] {n.Summary} (来源: {n.SourceId}, {n.Timestamp:HH:mm:ss})");
                }
                sb.AppendLine();
            }

            if (firedScheduledTasks.Count > 0)
            {
                sb.AppendLine($"--- 定时任务到期 ({firedScheduledTasks.Count}) ---");
                foreach (var st in firedScheduledTasks)
                {
                    sb.AppendLine($"  定时#{st.TaskId}: {st.Description}");
                    if (!string.IsNullOrEmpty(st.Payload))
                        sb.AppendLine($"    载荷: {st.Payload}");
                }
                sb.AppendLine();
            }

            return sb.ToString();
        }
    }

    /// <summary>定时任务到期事件（由 MasterEngine 投递）。</summary>
    internal class ScheduledTaskFiredEvent
    {
        public int TaskId { get; set; }
        public string Description { get; set; } = "";
        public string? Payload { get; set; }
    }
}
