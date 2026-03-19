using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AgentCoreProcesser.Models;
using System.IO;
using System.Text.Json.Nodes;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using AgentCoreProcesser.Client;

namespace AgentCoreProcesser.Core
{
    internal class Processer
    {
        public string cfgDirectionPath;

        public AIApiClient client = new();

        string cfgName = "base";

        public string CfgName
        {
            get => cfgName;
            set
            {
                // 查错
                if (!File.Exists(Path.Combine(cfgDirectionPath, value) + ".json"))
                {
                    Console.WriteLine($"Configuration file {value} not found in {cfgDirectionPath}.");
                    cfgName = "base";
                    return;
                }
                // 初始化
                cfgName = value;
                client.apiClientCfg = ApiClientCfg.FromJson(File.ReadAllText(Path.Combine(cfgDirectionPath, cfgName) + ".json"));
            }
        }

        public Processer(string cfgName="base", string cfgDirectionPath = "E:\\Workspace\\AgentLilaraProject\\Storage\\Core")
        {
            this.cfgDirectionPath = cfgDirectionPath;
            CfgName = cfgName;
        }

        public async Task ProcessAsync(Action<ApiResponse> OnDelta)
        {

            // 调用模型
            using var cts = new CancellationTokenSource();
            await client.StreamChatAsync(OnDelta, cts.Token).ConfigureAwait(false);
        }
    }

    public struct ProcessBody
    {
        public string Prompt { get; set; }
    }
}
