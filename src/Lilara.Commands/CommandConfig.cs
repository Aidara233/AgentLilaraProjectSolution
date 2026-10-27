using System.IO;
using Newtonsoft.Json;

namespace AgentCoreProcessor.Command
{
    /// <summary>
    /// 命令系统配置。从 Storage/Command/CommandConfig.json 加载。
    /// </summary>
    internal class CommandConfig
    {
        /// <summary>命令前缀，默认 "/"</summary>
        public string Prefix { get; set; } = "/";

        public static CommandConfig Load(string path)
        {
            if (!File.Exists(path)) return new CommandConfig();
            var json = File.ReadAllText(path);
            return JsonConvert.DeserializeObject<CommandConfig>(json) ?? new CommandConfig();
        }
    }
}
