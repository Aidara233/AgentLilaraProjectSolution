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
        }

        public async Task<string> GenerateAsync(string fragmentSummary, string? recentContext = null)
        {
            ResetProcessor();
            var sb = new StringBuilder();
            sb.Append($"正在梦到：{fragmentSummary}");
            if (!string.IsNullOrEmpty(recentContext))
                sb.Append($"\n最近的对话片段：{recentContext}");
            return await GenerateOnceAsync(sb.ToString());
        }
    }
}
