using System;
using System.Collections.Generic;
using AgentCoreProcessor.WebUI.Components.Shared;

namespace AgentCoreProcessor.WebUI.Services.Alerts
{
    internal class EngineAlertProvider : IAlertProvider
    {
        public IEnumerable<AlertItem> GetAlerts(SystemSnapshot snapshot)
        {
            // SystemEngine 未运行
            if (snapshot.SystemEngine == null || !snapshot.SystemEngine.IsAlive)
            {
                yield return new AlertItem
                {
                    Level = AlertLevel.Error,
                    Source = "引擎",
                    Message = "SystemEngine 未运行",
                    LinkHref = "/engine/manage"
                };
            }
            else
            {
                // 睡觉请求待审批
                if (snapshot.SystemEngine.HasPendingSleepRequest)
                {
                    var elapsed = snapshot.SystemEngine.SleepRequestTime.HasValue
                        ? (DateTime.Now - snapshot.SystemEngine.SleepRequestTime.Value).TotalMinutes
                        : 0;
                    yield return new AlertItem
                    {
                        Level = AlertLevel.Warning,
                        Source = "睡觉",
                        Message = $"睡觉请求待审批 ({elapsed:F0} 分钟前)",
                        LinkHref = "/engine/system/sleep"
                    };
                }

                // 任务队列积压
                if (snapshot.SystemEngine.TaskQueueDepth > 5)
                {
                    yield return new AlertItem
                    {
                        Level = AlertLevel.Warning,
                        Source = "任务",
                        Message = $"任务队列积压: {snapshot.SystemEngine.TaskQueueDepth} 个待处理",
                        LinkHref = "/engine/system/tasks"
                    };
                }
            }
        }
    }
}
