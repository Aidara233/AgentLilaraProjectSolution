using System.Linq;
using System.Threading.Tasks;
using AgentCoreProcessor.Engine.Mind;
using AgentLilara.PluginSDK.Services;

namespace AgentCoreProcessor.Tool.Host
{
    /// <summary>
    /// IReviewStageAccess 桥接实现。映射 ReviewStage 到 SDK。
    /// </summary>
    internal class ReviewStageAccessImpl : IReviewStageAccess
    {
        private readonly ReviewStage _stage;

        public ReviewStageAccessImpl(ReviewStage stage) => _stage = stage;

        public void StageCredibility(int claimId, float newCredibility)
            => _stage.StageCredibility(claimId, newCredibility);

        public async Task<ReviewPreviewDto> PreviewAsync()
        {
            var result = await _stage.PreviewAsync();
            return new ReviewPreviewDto
            {
                Ok = result.Ok,
                Message = result.Message,
                AffectedClaims = result.AffectedClaims,
                OverBudgetGroups = result.OverBudgetGroups,
            };
        }

        public async Task<ReviewCommitDto> CommitAsync()
        {
            var result = await _stage.CommitAsync();
            return new ReviewCommitDto
            {
                Success = result.Success,
                Error = result.Error,
                CommittedChanges = result.CommittedChanges,
                OverBudgetGroups = result.OverBudgetGroups,
            };
        }

        public void Clear() => _stage.Clear();
    }
}
