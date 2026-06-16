using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Config;
using AgentCoreProcessor.Logging;
using AgentCoreProcessor.Engine;
using AgentCoreProcessor.Models;
using AgentCoreProcessor.Client;

namespace AgentCoreProcessor.Core
{
    internal class Processor
    {
        private static string DefaultCfgPath => PathConfig.CoreConfigPath;

        /// <summary>当前 Processor 加载的基础提示词（promptFiles），供 AgentCore 注入。</summary>
        public string? BasePrompt { get; private set; }

        private string cfgDirectoryPath;

        private IModelClient client = new OpenAIModelClient();

        private string cfgName = "Base";

        // 配置缓存，避免频繁读取文件
        private readonly Dictionary<string, (ApiClientCfg Config, IModelClient Client)> cfgCache = new();

        public IModelClient Client => client;

        public string CfgName
        {
            get => cfgName;
            set
            {
                var fullPath = Path.Combine(cfgDirectoryPath, value) + ".json";
                if (!File.Exists(fullPath))
                {
                    Signal.Warn(LogGroup.Engine, "Core配置文件缺失，回退到Base", new { requested = value });
                    cfgName = "Base";
                    return;
                }

                // 如果已缓存，直接使用
                if (cfgCache.TryGetValue(value, out var cached))
                {
                    cfgName = value;
                    client = cached.Client;
                    LoadBasePrompt();
                    return;
                }

                cfgName = value;
                var cfg = ApiClientCfg.FromJson(File.ReadAllText(Path.Combine(cfgDirectoryPath, cfgName) + ".json"));
                client = ModelClientFactory.Create(cfg);

                // 缓存配置和客户端
                cfgCache[value] = (cfg, client);

                LoadBasePrompt();
            }
        }

        public Processor(string cfgName = "Base", string? cfgDirectoryPath = null)
        {
            this.cfgDirectoryPath = cfgDirectoryPath ?? DefaultCfgPath;
            CfgName = cfgName;
        }

        public async Task ProcessAsync(Action<ApiResponse> onDelta, CancellationToken ct = default,
            Action? onRetryReset = null)
        {
            try
            {
                await client.StreamChatAsync(onDelta, ct).ConfigureAwait(false);
            }
            catch (Exception)
            {
                var cfg = client.Config;
                var context = $"core={cfgName} provider={cfg.Provider} model={cfg.Model} endpoint={cfg.ApiEndpoint}";

                // 重试一次：先让调用方清空已累积的部分内容
                onRetryReset?.Invoke();
                try
                {
                    await client.StreamChatAsync(onDelta, ct).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    throw;
                }
            }
        }

        /// <summary>
        /// 从 promptFiles 加载系统提示词文件，按顺序拼接。
        /// 结果存入 BasePrompt，供 AgentCore 注入到对话开头。
        /// </summary>
        private void LoadBasePrompt()
        {
            var cfg = client.Config;
            if (cfg.PromptFiles == null || cfg.PromptFiles.Count == 0) return;

            var templatesDir = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "templates"));
            var parts = new List<string>();
            foreach (var file in cfg.PromptFiles)
            {
                var content = PromptLoader.Load(file, cfgDirectoryPath, templatesDir);
                if (!string.IsNullOrEmpty(content))
                    parts.Add(content);
            }

            if (parts.Count == 0) return;
            var promptContent = string.Join("\n\n", parts);

            BasePrompt = promptContent;
            client.AddSystemMessage(promptContent);
        }
    }
}
