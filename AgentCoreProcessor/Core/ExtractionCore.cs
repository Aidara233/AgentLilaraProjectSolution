using System.IO;
using System.Threading.Tasks;
using AgentCoreProcessor.Config;
using AgentCoreProcessor.Engine.Mind;

namespace AgentCoreProcessor.Core
{
    /// <summary>
    /// 提取核心。单轮、无工具、无状态：把攒批消息文本喂进去，拿回断言 JSON。
    /// 指令模板在 Storage/Core/Prompts/ExtractionPrompt.txt（可热改），调用方只提供 {{MESSAGES}} 部分。
    /// 实现 <see cref="IExtractionLlm"/> 供 ExtractService / ExpressExtractor 消费。
    /// </summary>
    internal class ExtractionCore : CoreBase, IExtractionLlm
    {
        public ExtractionCore() : base("ExtractionCore")
        {
            CallerTag = "Extraction";
        }

        /// <summary>给定攒批消息文本，返回断言 JSON 原始文本。异常向上抛，由调用方容错。</summary>
        public async Task<string?> ExtractClaimsAsync(string context)
        {
            ResetProcessor();
            var prompt = LoadPromptTemplate().Replace("{{MESSAGES}}", context);
            return await GenerateOnceAsync(prompt);
        }

        private static string LoadPromptTemplate()
        {
            var templatesDir = Path.GetFullPath(
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "templates"));
            return PromptLoader.Load("ExtractionPrompt.txt", PathConfig.StoragePath, templatesDir)
                   ?? "从下面的对话中提取值得记住的事实，输出 JSON：{\"claims\":[]}\n\n{{MESSAGES}}";
        }
    }
}
