using System.Collections.Generic;
using AgentCoreProcessor.WebUI.Components.Shared;

namespace AgentCoreProcessor.WebUI.Services
{
    internal interface IAlertProvider
    {
        IEnumerable<AlertItem> GetAlerts(SystemSnapshot snapshot);
    }
}
