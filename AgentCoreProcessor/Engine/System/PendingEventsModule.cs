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
    }

    /// <summary>定时任务到期事件（由 MasterEngine 投递）。</summary>
    internal class ScheduledTaskFiredEvent
    {
        public int TaskId { get; set; }
        public string Description { get; set; } = "";
        public string? Payload { get; set; }
    }
}
