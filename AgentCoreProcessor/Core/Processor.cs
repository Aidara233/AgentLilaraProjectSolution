using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Config;
using AgentCoreProcessor.Models;
using AgentCoreProcessor.Client;

namespace AgentCoreProcessor.Core
{
    internal class Processor
    {
        private static string DefaultCfgPath => PathConfig.CoreConfigPath;

        private string cfgDirectoryPath;

        private IModelClient client = new OpenAIModelClient();

        private string cfgName = "Base";

        public IModelClient Client => client;

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
                var cfg = ApiClientCfg.FromJson(File.ReadAllText(Path.Combine(cfgDirectoryPath, cfgName) + ".json"));
                client = ModelClientFactory.Create(cfg);
            }
        }

        public bool UsePersona { get; }

        public Processor(string cfgName = "Base", string? cfgDirectoryPath = null, bool usePersona = true)
        {
            this.cfgDirectoryPath = cfgDirectoryPath ?? DefaultCfgPath;
            UsePersona = usePersona;
            CfgName = cfgName;
            if (usePersona) InjectPersona();
        }

        public async Task ProcessAsync(Action<ApiResponse> onDelta, CancellationToken ct = default)
        {
            await client.StreamChatAsync(onDelta, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// 检查 Persona.txt 是否存在，存在则注入到 PresetMessages 的第一条 system 消息前。
        /// </summary>
        private void InjectPersona()
        {
            var personaPath = Path.Combine(cfgDirectoryPath, "Persona.txt");
            if (!File.Exists(personaPath)) return;

            var persona = File.ReadAllText(personaPath).Trim();
            if (string.IsNullOrEmpty(persona)) return;

            var presets = client.Config.PresetMessages;
            var systemMsg = presets.Find(m => m.Role == "system");
            if (systemMsg != null)
            {
                systemMsg.Content = persona + "\n\n" + systemMsg.Content;
            }
            else
            {
                presets.Insert(0, new Message { Role = "system", Content = persona });
            }
        }
    }
}
