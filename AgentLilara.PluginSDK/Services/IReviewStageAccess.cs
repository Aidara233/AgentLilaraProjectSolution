using System.Collections.Generic;
using System.Threading.Tasks;

namespace AgentLilara.PluginSDK.Services
{
    /// <summary>
    /// Review 暂存区访问接口（git 式提交）。
    /// 改动进暂存区 → preview 预览 → commit 提交。
    /// 支持三类暂存操作：
    ///   1. 可信度调整（StageCredibility）— 拉开可信度差距
    ///   2. 解除属性排他（StageExclusivityRemoval）— 该属性名今后不再触发冲突检测
    ///   3. 拆解冲突组（StageGroupResolution）— 标记本组已解决，组内约束解除
    /// </summary>
    public interface IReviewStageAccess
    {
        /// <summary>把一条断言的可信度锚点调值进暂存区。</summary>
        void StageCredibility(int claimId, float newCredibility);

        /// <summary>把一个属性关键词的排他性解除操作加进暂存区（IsExclusive → false）。</summary>
        void StageExclusivityRemoval(string keyword);

        /// <summary>把一个冲突组的解决操作加进暂存区（标记 ResolvedAt，解除组内预算约束）。</summary>
        void StageGroupResolution(int groupId);

        /// <summary>预览影响（dry-run，不写库）。</summary>
        Task<ReviewPreviewDto> PreviewAsync();

        /// <summary>提交暂存区（原子事务落库 + 重算 + 校验和 ≤1）。</summary>
        Task<ReviewCommitDto> CommitAsync();

        /// <summary>清空暂存区。</summary>
        void Clear();
    }

    /// <summary>preview 结果。</summary>
    public class ReviewPreviewDto
    {
        public bool Ok { get; set; }
        public string Message { get; set; } = "";
        public List<int> AffectedClaims { get; set; } = new();
        public List<int> OverBudgetGroups { get; set; } = new();
    }

    /// <summary>commit 结果。</summary>
    public class ReviewCommitDto
    {
        public bool Success { get; set; }
        public string Error { get; set; } = "";
        public int CommittedChanges { get; set; }
        public List<int> OverBudgetGroups { get; set; } = new();
    }
}
