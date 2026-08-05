using System.Collections.Generic;
using System.Threading.Tasks;

namespace AgentLilara.PluginSDK.Services
{
    /// <summary>
    /// Review 暂存区访问接口（git 式提交）。
    /// 改动进暂存区 → preview 预览 → commit 提交。
    /// </summary>
    public interface IReviewStageAccess
    {
        /// <summary>把一条断言的可信度锚点调值进暂存区。</summary>
        void StageCredibility(int claimId, float newCredibility);

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
