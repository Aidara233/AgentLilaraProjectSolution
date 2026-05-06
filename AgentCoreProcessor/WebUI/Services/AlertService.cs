using System.Collections.Generic;
using System.Linq;
using AgentCoreProcessor.WebUI.Components.Shared;

namespace AgentCoreProcessor.WebUI.Services
{
    internal class AlertService
    {
        private readonly List<IAlertProvider> providers = new();

        public void Register(IAlertProvider provider)
        {
            providers.Add(provider);
        }

        public List<AlertItem> CollectAlerts(SystemSnapshot snapshot)
        {
            return providers.SelectMany(p => p.GetAlerts(snapshot)).ToList();
        }
    }
}
