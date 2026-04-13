using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using AgentCoreProcessor.Database;

namespace AgentCoreProcessor.Core
{
    /// <summary>
    /// 记忆组合。从强关联记忆中抽象推理，生成衍生记忆。
    /// </summary>
    internal class CombineCore : CoreBase
    {
        /// <summary>
        /// 模型从一组关联记忆中提炼出新的洞察/结论。
        /// 输出：衍生记忆的内容文本，或 "none" 表示无法产生有价值的组合。
        /// </summary>
        public async Task<string> CombineAsync(List<MemoryEntry> relatedMemories)
        {
            ResetProcessor();
            var sb = new StringBuilder();
            sb.AppendLine("以下记忆之间存在较强关联，请尝试从中提炼出新的洞察或结论：");
            for (int i = 0; i < relatedMemories.Count; i++)
                sb.AppendLine($"[{i}] {relatedMemories[i].Content}");
            return await GenerateOnceAsync(sb.ToString());
        }
    }
}
