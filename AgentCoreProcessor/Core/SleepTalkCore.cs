using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AgentCoreProcessor.Config;

namespace AgentCoreProcessor.Core
{
    internal class SleepTalkCore : CoreBase
    {
        public SleepTalkCore() : base("SleepTalkCore")
        {
        }

        public async Task<string> GenerateAsync(List<string> fragments)
        {
            ResetProcessor();
            var template = LoadPromptTemplate();
            var fragmentList = fragments
                .Where(f => !string.IsNullOrWhiteSpace(f))
                .Select(f => f.Trim())
                .ToList();
            var fragmentsBlock = string.Join("\n", fragmentList.Select(f => $"- \"{f}\""));
            var prompt = template.Replace("{{FRAGMENTS}}", fragmentsBlock);
            return await GenerateOnceAsync(prompt);
        }

        private static string LoadPromptTemplate()
        {
            var coreDir = PathConfig.CoreConfigPath;
            var templatesDir = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "templates"));
            return PromptLoader.Load("SleepTalkPrompt.txt", coreDir, templatesDir)
                   ?? "你正在睡觉做梦。下面是一些乱七八糟的碎片：\n{{FRAGMENTS}}\n\n请把它们搅在一起说一句梦话。";
        }
    }
}
