using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using AgentCoreProcessor.Database;

namespace AgentCoreProcessor.Core
{
    /// <summary>
    /// 临时记忆整合。读取临时库内容，由模型判断去重/合并/入库/丢弃。
    /// 仅在小睡/大睡时执行。
    /// </summary>
    internal class ConsolidationCore : CoreBase
    {
        protected override bool UsePersona => false;
        /// <summary>
        /// 模型判断临时记忆应如何处理。
        /// 输入：临时记忆列表 + 已有主库记忆（用于去重参考）。
        /// 输出：JSON 数组，每条记忆的处理方式（keep/merge/discard）+ 合并后内容。
        /// </summary>
        public async Task<string> ConsolidateAsync(
            List<TempMemoryEntry> tempEntries,
            List<MemoryEntry> existingMemories)
        {
            ResetProcessor();
            var sb = new StringBuilder();

            sb.AppendLine("临时记忆列表：");
            for (int i = 0; i < tempEntries.Count; i++)
            {
                var t = tempEntries[i];
                sb.AppendLine($"[{i}] {t.Content}");
            }

            if (existingMemories.Count > 0)
            {
                sb.AppendLine("\n已有主库记忆（参考去重）：");
                foreach (var m in existingMemories)
                    sb.AppendLine($"- {m.Content}");
            }

            return await GenerateOnceAsync(sb.ToString());
        }
    }
}
