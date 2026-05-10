using System.Text;
using System.Threading.Tasks;

namespace AgentCoreProcessor.Core
{
    /// <summary>
    /// 梦话 Core。根据当前做梦片段的内容生成短小、梦幻的呓语。
    /// UsePersona=true，保持角色一致性。
    /// </summary>
    internal class SleepTalkCore : CoreBase
    {
        protected override bool UsePersona => true;

        public SleepTalkCore() : base("SleepTalkCore")
        {
            ApplyExtraMessages();
        }

        public async Task<string> GenerateAsync(string fragmentSummary, string? recentContext = null)
        {
            var sb = new StringBuilder();
            sb.AppendLine("你正在睡觉做梦。你需要说一句梦话。");
            sb.AppendLine();
            sb.AppendLine("**要求**：");
            sb.AppendLine("- 一句话，最多 30 字");
            sb.AppendLine("- 像真正的梦话：片段化、朦胧、可能语无伦次");
            sb.AppendLine("- 和你正在梦到的内容有隐约关联，但不要直白复述");
            sb.AppendLine("- 可以是喃喃自语、半句话、感叹、或模糊的意象");
            sb.AppendLine("- 不要用引号包裹，直接输出梦话内容");
            sb.AppendLine();
            sb.AppendLine($"**你正在梦到的内容**：{fragmentSummary}");

            if (!string.IsNullOrEmpty(recentContext))
            {
                sb.AppendLine($"**最近的对话片段**：{recentContext}");
            }

            return await GenerateOnceAsync(sb.ToString());
        }
    }
}
