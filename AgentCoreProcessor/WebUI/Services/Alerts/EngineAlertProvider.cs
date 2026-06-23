using System;
using System.Collections.Generic;
using System.Linq;
using AgentCoreProcessor.Engine;
using AgentCoreProcessor.WebUI.Components.Shared;

namespace AgentCoreProcessor.WebUI.Services.Alerts
{
    internal class EngineAlertProvider : IAlertProvider
    {
        private readonly MasterEngine _engine;
        public EngineAlertProvider(MasterEngine engine) => _engine = engine;

        public IEnumerable<AlertItem> GetAlerts()
        {
            // SessionEngine 状态
            var sessionEngine = _engine.GetActiveEnginesSnapshot()
                .OfType<AgentCoreProcessor.Engine.Session.SessionEngine>().FirstOrDefault();

            if (sessionEngine == null || !sessionEngine.IsAlive)
            {
                yield return new AlertItem
                {
                    Level = AlertLevel.Warning,
                    Source = "引擎",
                    Message = "SessionEngine 未运行"
                };
            }
            else
            {
                // 会话过多告警
                var sessionCount = sessionEngine.GetActiveSessions().Count;
                if (sessionCount > 10)
                {
                    yield return new AlertItem
                    {
                        Level = AlertLevel.Warning,
                        Source = "会话",
                        Message = $"活跃会话过多: {sessionCount} 个"
                    };
                }

                // 待办积压
                var pendingTodos = sessionEngine.GetPendingTodos();
                if (pendingTodos.Count > 5)
                {
                    yield return new AlertItem
                    {
                        Level = AlertLevel.Warning,
                        Source = "任务",
                        Message = $"待办事项积压: {pendingTodos.Count} 个"
                    };
                }
            }

            // 频道循环 API 错误
            var workerCheck = _engine.GetSpawnCheck<ChannelEngineSpawnCheck>();
            var workers = new List<WorkerSnapshot>();
            if (workerCheck != null)
            {
                foreach (var (_, w) in workerCheck.GetActiveChannels())
                {
                    if (!w.IsAlive) continue;
                    workers.Add(w.GetSnapshot());
                }
            }

            var errorWorkers = workers.Where(w => w.HasRecentError).ToList();
            if (errorWorkers.Count > 0)
            {
                var worst = errorWorkers.OrderByDescending(w => w.ConsecutiveFailures).First();
                yield return new AlertItem
                {
                    Level = worst.ConsecutiveFailures >= 3 ? AlertLevel.Error : AlertLevel.Warning,
                    Source = "频道循环",
                    Message = errorWorkers.Count == 1
                        ? $"频道 #{worst.ChannelId} API 失败 {worst.ConsecutiveFailures} 次"
                        : $"{errorWorkers.Count} 个频道出现 API 错误",
                    LinkHref = $"/p/channels/{worst.ChannelId}"
                };
            }
        }
    }
}
