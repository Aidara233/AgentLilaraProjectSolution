using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AgentCoreProcessor.Adapter;
using AgentLilara.PluginSDK.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AgentCoreProcessor.Tool.Host
{
    internal class AdapterAccessImpl : IAdapterAccess
    {
        private readonly AdapterManager _adapterManager;

        public AdapterAccessImpl(AdapterManager adapterManager)
        {
            _adapterManager = adapterManager;
        }

        public string? GetBotPlatformId(string platform)
            => _adapterManager.GetBotPlatformId(platform);

        public string? GetAdapterIdForChannel(string channelId)
            => _adapterManager.ResolveByChannelId(channelId)?.Id;

        public List<AdapterActionInfo> GetAvailableActions(string adapterId)
        {
            var adapter = _adapterManager.GetAdapterById(adapterId);
            if (adapter == null) return new();

            return adapter.GetAvailableActions().Select(a => new AdapterActionInfo
            {
                Name = a.Name,
                Label = a.Label,
                Description = a.Description,
                Params = a.Params.Select(p => new ActionParamInfo
                {
                    Name = p.Name,
                    Label = p.Label,
                    Type = p.Type,
                    Required = p.Required
                }).ToList()
            }).ToList();
        }

        public async Task<string?> ExecuteActionAsync(string adapterId, string action, string? paramsJson = null)
        {
            var adapter = _adapterManager.GetAdapterById(adapterId);
            if (adapter == null) return null;

            var parameters = new Dictionary<string, string>();
            if (!string.IsNullOrEmpty(paramsJson))
            {
                try
                {
                    var obj = JsonConvert.DeserializeObject<JObject>(paramsJson);
                    if (obj != null)
                    {
                        foreach (var kv in obj)
                            parameters[kv.Key] = kv.Value?.ToString() ?? "";
                    }
                }
                catch { return null; }
            }

            var result = await adapter.ExecuteActionAsync(action, parameters);
            return result.Success ? (result.Result ?? "OK") : null;
        }
    }
}
