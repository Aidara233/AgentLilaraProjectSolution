using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using AgentCoreProcessor.Engine;

namespace AgentCoreProcessor.Engine.Modules
{
    /// <summary>
    /// 待处理事件模块。每轮收集所有待处理事件（任务、通知、定时任务到期、跨循环请求），格式化注入 prompt。
    /// </summary>
    internal class PendingEventsModule : EngineModule
    {
        public override string Name => "待处理事件";

        private readonly List<SystemTask> pendingTasks = new();
        private readonly List<Notification> pendingNotifications = new();
        private readonly List<ScheduledTaskFiredEvent> firedScheduledTasks = new();
        private readonly List<CrossRequest> pendingCrossRequests = new();
        private bool noActionLastRound;

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

        public void SetPendingCrossRequests(List<CrossRequest> requests)
        {
            pendingCrossRequests.Clear();
            pendingCrossRequests.AddRange(requests);
        }

        public override void Attach(ILoopBus bus) { }

        public override Task<string?> BuildRoundInjectAsync(InjectContext ctx)
        {
            var sb = new StringBuilder();

            // 跨循环请求
            if (pendingCrossRequests.Count > 0)
            {
                sb.AppendLine("[跨循环请求]");
                foreach (var r in pendingCrossRequests)
                {
                    var targetStr = r.TargetId ?? "广播";
                    sb.AppendLine($"- 请求#{r.RequestId[..8]}: {r.Title}");
                    sb.AppendLine($"  发起者: {r.InitiatorId} | 目标: {targetStr} | 超时: {r.ExpiresAt:HH:mm:ss}");
                    sb.AppendLine($"  内容: {r.Content.Truncate(200)}");
                    if (r.Responses.Count > 0)
                    {
                        var lastResp = r.Responses.Last();
                        sb.AppendLine($"  最近回应: [{lastResp.Type}] {lastResp.Content.Truncate(100)}");
                    }
                }
                sb.AppendLine();
            }

            // 任务队列
            if (pendingTasks.Count > 0)
            {
                sb.AppendLine("[待处理任务]");
                foreach (var t in pendingTasks)
                    sb.AppendLine($"- 任务:{t.Description} (频道{t.SourceChannelId}, 优先级{t.Priority})");
                sb.AppendLine();
            }

            // 通知
            if (pendingNotifications.Count > 0)
            {
                sb.AppendLine("[待处理通知]");
                foreach (var n in pendingNotifications)
                    sb.AppendLine($"- [{n.Type}] {n.Summary}");
                sb.AppendLine();
            }

            // 定时任务
            if (firedScheduledTasks.Count > 0)
            {
                sb.AppendLine("[到期的定时任务]");
                foreach (var t in firedScheduledTasks)
                    sb.AppendLine($"- 任务#{t.TaskId}: {t.Description}");
                sb.AppendLine();
            }

            return sb.Length > 0 ? Task.FromResult<string?>(sb.ToString()) : Task.FromResult<string?>(null);
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
