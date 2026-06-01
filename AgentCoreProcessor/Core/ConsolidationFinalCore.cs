using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using AgentCoreProcessor.Database;

namespace AgentCoreProcessor.Core
{
    /// <summary>
    /// 记忆整合第二轮：跨组去重、最终合并。
    /// 输入为第一轮各批次的产物，输出最终入库决策。
    /// </summary>
    internal class ConsolidationFinalCore : CoreBase
    {
        protected override bool UsePersona => false;

        public async Task<string> FinalizeAsync(
            List<ConsolidationCandidate> candidates,
            List<MemoryEntry> existingMemories)
        {
            ResetProcessor();
            var sb = new StringBuilder();

            sb.AppendLine("待入库记忆列表：");
            for (int i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                var meta = c.PersonId.HasValue ? $" (person={c.PersonId})" : "";
                sb.AppendLine($"[{i}] {c.Content}{meta}");
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

    /// <summary>
    /// 第一轮整合产物，携带元数据供第二轮处理。
    /// </summary>
    internal class ConsolidationCandidate
    {
        public string Content { get; set; } = "";
        public int? PersonId { get; set; }
        public int? ChannelId { get; set; }
        public string? Type { get; set; }
        public string? Subject { get; set; }
        public float Certainty { get; set; } = 1.0f;
    }
}
