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

            // 会话崩溃告警（express 会话化后无频道引擎，错误经 Signal/会话状态追踪）
            var crashed = sessionEngine?.GetActiveSessions()
                .Where(s => s.Status == AgentLilara.PluginSDK.SessionStatus.Crashed)
                .ToList();
            if (crashed != null && crashed.Count > 0)
            {
                yield return new AlertItem
                {
                    Level = AlertLevel.Warning,
                    Source = "会话",
                    Message = $"{crashed.Count} 个会话处于崩溃状态",
                    LinkHref = "/p/sessions"
                };
            }
        }
    }
}
