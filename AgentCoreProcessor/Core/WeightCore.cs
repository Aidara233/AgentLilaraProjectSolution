using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using AgentCoreProcessor.Database;

namespace AgentCoreProcessor.Core
{
    /// <summary>
    /// 记忆权重调整。评估记忆的重要性，调整 Importance 值。
    /// </summary>
    internal class WeightCore : CoreBase
    {
        /// <summary>
        /// 模型评估一批记忆的重要性。
        /// 输出：JSON 数组，每条记忆的新 Importance 值 (0.0-1.0)。
        /// 低于阈值的记忆会被标记为可过期。
        /// </summary>
        public async Task<string> EvaluateAsync(List<MemoryEntry> memories)
        {
            ResetProcessor();
            var sb = new StringBuilder();
            sb.AppendLine("请评估以下记忆的重要性：");
            for (int i = 0; i < memories.Count; i++)
            {
                var m = memories[i];
                sb.AppendLine($"[{i}] (当前重要性={m.Importance:F2}) {m.Content}");
            }
            return await GenerateOnceAsync(sb.ToString());
        }
    }
}
