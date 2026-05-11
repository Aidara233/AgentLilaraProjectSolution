using System;
using System.Collections.Generic;
using System.Linq;
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
                    Message = "SystemEngine 未运行" +
                        (snapshot.SystemEngine?.RestartCount > 0
                            ? $" (已重启 {snapshot.SystemEngine.RestartCount} 次)"
                            : ""),
                    LinkHref = "/engine/system"
                };
            }
            else
            {
                // 系统循环 API 错误
                if (snapshot.SystemEngine.HasRecentError)
                {
                    yield return new AlertItem
                    {
                        Level = snapshot.SystemEngine.ConsecutiveFailures >= 3
                            ? AlertLevel.Error : AlertLevel.Warning,
                        Source = "系统循环",
                        Message = $"API 连续失败 {snapshot.SystemEngine.ConsecutiveFailures} 次: {Truncate(snapshot.SystemEngine.LastErrorMessage, 80)}",
                        LinkHref = "/engine/system"
                    };
                }

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

            // 频道循环 API 错误
            var errorWorkers = snapshot.Workers.Where(w => w.HasRecentError).ToList();
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
                    LinkHref = $"/engine/channels/{worst.ChannelId}"
                };
            }
        }
        private static string Truncate(string? s, int maxLen)
            => s == null ? "" : s.Length <= maxLen ? s : s[..maxLen] + "...";
    }
}
