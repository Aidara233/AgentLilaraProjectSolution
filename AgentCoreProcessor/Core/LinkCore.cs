using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using AgentCoreProcessor.Database;

namespace AgentCoreProcessor.Core
{
    /// <summary>
    /// 关联重建。分析记忆之间的关系，建立/更新 MemoryLink。
    /// </summary>
    internal class LinkCore : CoreBase
    {
        /// <summary>
        /// 模型分析一条新记忆与候选记忆之间的关联关系。
        /// 输出：JSON 数组，关联的候选记忆索引 + 关联类型 + 强度。
        /// </summary>
        public async Task<string> AnalyzeLinksAsync(
            MemoryEntry target,
            List<MemoryEntry> candidates)
        {
            ResetProcessor();
            var sb = new StringBuilder();
            sb.AppendLine($"目标记忆：{target.Content}");
            sb.AppendLine("\n候选记忆：");
            for (int i = 0; i < candidates.Count; i++)
                sb.AppendLine($"[{i}] {candidates[i].Content}");
            return await GenerateOnceAsync(sb.ToString());
        }
    }
}
