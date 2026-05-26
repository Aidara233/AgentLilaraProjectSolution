using System.Collections.Generic;
using System.Linq;
using AgentCoreProcessor.WebUI.Components.Shared;

namespace AgentCoreProcessor.WebUI.Services
{
    internal class AlertService
    {
        private readonly List<IAlertProvider> providers = new();
        private readonly object providerLock = new();

        public void Register(IAlertProvider provider)
        {
            lock (providerLock) { providers.Add(provider); }
        }

        public List<AlertItem> CollectAlerts(SystemSnapshot snapshot)
        {
            List<IAlertProvider> providerList;
            lock (providerLock) { providerList = providers.ToList(); }
            return providerList.SelectMany(p => p.GetAlerts(snapshot)).ToList();
        }
    }
}
