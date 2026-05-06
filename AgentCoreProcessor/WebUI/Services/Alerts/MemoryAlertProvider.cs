using System.Collections.Generic;
using System.Linq;
using AgentCoreProcessor.WebUI.Components.Shared;

namespace AgentCoreProcessor.WebUI.Services.Alerts
{
    internal class MemoryAlertProvider : IAlertProvider
    {
        public IEnumerable<AlertItem> GetAlerts(SystemSnapshot snapshot)
        {
            // 未来可以从 snapshot 中获取记忆统计
            // 目前作为占位，后续接入记忆数据后启用
            yield break;
        }
    }
}
