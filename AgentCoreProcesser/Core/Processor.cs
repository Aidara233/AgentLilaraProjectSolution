using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcesser.Models;
using AgentCoreProcesser.Client;

namespace AgentCoreProcesser.Core
{
    internal class Processor
    {
        private static readonly string DefaultCfgPath =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "Storage", "Core");

        private string cfgDirectoryPath;

        private AIApiClient client = new();

        private string cfgName = "Base";

        public AIApiClient Client => client;

        public string CfgName
        {
            get => cfgName;
            set
            {
                if (!File.Exists(Path.Combine(cfgDirectoryPath, value) + ".json"))
                {
                    cfgName = "Base";
                    return;
                }
                cfgName = value;
                client.Config = ApiClientCfg.FromJson(File.ReadAllText(Path.Combine(cfgDirectoryPath, cfgName) + ".json"));
            }
        }

        public Processor(string cfgName = "Base", string? cfgDirectoryPath = null)
        {
            this.cfgDirectoryPath = cfgDirectoryPath ?? DefaultCfgPath;
            CfgName = cfgName;
        }

        public async Task ProcessAsync(Action<ApiResponse> onDelta, CancellationToken ct = default)
        {
            await client.StreamChatAsync(onDelta, ct).ConfigureAwait(false);
        }
    }
}
