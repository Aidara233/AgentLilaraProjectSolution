using System.Collections.Generic;
using System.Linq;
using AgentCoreProcessor.WebUI.Components.Shared;

namespace AgentCoreProcessor.WebUI.Services.Alerts
{
    internal class MemoryAlertProvider : IAlertProvider
    {
        public IEnumerable<AlertItem> GetAlerts()
        {
            // 记忆统计告警（暂为占位）
            yield break;
        }
    }
}
