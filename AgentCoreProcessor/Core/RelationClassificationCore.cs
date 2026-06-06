using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using AgentCoreProcessor.Database;

namespace AgentCoreProcessor.Core
{
    /// <summary>
    /// 关系分类核心。判断中心节点与多个候选目标节点的语义关系。
    /// 输出 support 值（-1.0~1.0），正值=关联，负值=矛盾，≈0=无关。
    /// </summary>
    internal class RelationClassificationCore : CoreBase
    {
        /// <summary>
        /// 分类中心节点与一批候选的语义关系。
        /// </summary>
        /// <param name="center">中心节点</param>
        /// <param name="targets">候选目标列表（最多 RelationBatchMaxTargets 个）</param>
        /// <param name="cosScores">每个候选与中心的 cosine 相似度，长度与 targets 一致</param>
        /// <returns>模型原始输出（JSON），由调用方解析</returns>
        public async Task<string> ClassifyAsync(
            MemoryEntry center,
            List<MemoryEntry> targets,
            List<float> cosScores)
        {
            ResetProcessor();
            var sb = new StringBuilder();

            sb.AppendLine("## 中心记忆");
            sb.AppendLine($"- ID: {center.Id}");
            sb.AppendLine($"- 内容: {center.Content}");
            sb.AppendLine($"- 类型: {center.Type ?? "fact"}");
            sb.AppendLine($"- 确定性: {center.Certainty:F2}");
            sb.AppendLine();

            sb.AppendLine("## 候选记忆（判断每条与中心记忆的语义关系）");
            sb.AppendLine("支持度(support): 正=两个说法一致或相互印证, 负=两个说法矛盾不可能同时成立, 0=无关");
            sb.AppendLine("参考\"相似度\"值：高相似度意味着两句话在讨论同一件事，但你需要判断它们是一致的还是在说不同的事。");
            sb.AppendLine();
            for (int i = 0; i < targets.Count; i++)
            {
                var t = targets[i];
                var cos = i < cosScores.Count ? cosScores[i] : 0f;
                sb.AppendLine($"[{i}] ID={t.Id} 相似度={cos:F4}");
                sb.AppendLine($"    内容: {t.Content}");
                sb.AppendLine($"    类型: {t.Type ?? "fact"}, 确定性: {t.Certainty:F2}");
                sb.AppendLine();
            }

            sb.AppendLine("## 输出");
            sb.AppendLine("JSON数组，每项包含候选索引和support值。只输出有关联或矛盾的项，确定为无关的可省略。");
            sb.AppendLine("[{\"targetIndex\": N, \"support\": -1.0~1.0}]");
            sb.AppendLine("仅输出JSON，不要输出其他内容。");

            return await GenerateOnceAsync(sb.ToString());
        }
    }
}
