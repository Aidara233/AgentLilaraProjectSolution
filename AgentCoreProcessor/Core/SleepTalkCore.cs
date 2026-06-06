using System.Collections.Generic;
using System.Threading.Tasks;
using AgentCoreProcessor.Config;

namespace AgentCoreProcessor.Core
{
    /// <summary>
    /// 梦话 Core。根据当前做梦片段的内容生成短小、梦幻的呓语。
    /// </summary>
    internal class SleepTalkCore : CoreBase
    {
        public SleepTalkCore() : base("SleepTalkCore")
        {
        }

        public async Task<string> GenerateAsync(string fragmentSummary, string? recentContext = null)
        {
            ResetProcessor();
            var template = LoadPromptTemplate();
            var vars = new Dictionary<string, string>
            {
                ["FRAGMENT"] = fragmentSummary,
                ["RECENT_CONTEXT"] = recentContext ?? ""
            };
            var prompt = PromptLoader.ApplyVariables(template, vars);
            return await GenerateOnceAsync(prompt);
        }

        private static string LoadPromptTemplate()
        {
            var coreDir = PathConfig.CoreConfigPath;
            var templatesDir = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "templates"));
            return PromptLoader.Load("SleepTalkPrompt.txt", coreDir, templatesDir)
                   ?? "你正在睡觉做梦。正在梦到：{{FRAGMENT}}\n最近的对话片段：{{RECENT_CONTEXT}}";
        }
    }
}
