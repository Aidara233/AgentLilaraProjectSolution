using System.Collections.Generic;
using System.Threading.Tasks;

namespace AgentLilara.PluginSDK.Services
{
    /// <summary>
    /// 待验证队列访问接口。
    /// 入队（Enqueue）供产生端（mark_for_review 等）用；消费（Claim/Complete）供 Review 会话用。
    /// </summary>
    public interface IReviewQueueAccess
    {
        /// <summary>入队一项。已存在 Pending/UnderReview 同 target 项则跳过。返回队列项 Id。</summary>
        Task<int> EnqueueAsync(string targetRef, string targetKind, string reason, string source, string priority = "Normal");

        /// <summary>列出待处理项（按优先级 High>Normal>Low）。</summary>
        Task<List<ReviewQueueItemDto>> GetPendingAsync(int limit = 10);

        /// <summary>认领单个（Pending→UnderReview）。返回认领成功的项，null=无可认领。</summary>
        Task<ReviewQueueItemDto?> ClaimAsync(int id);

        /// <summary>完成一项（→Done）。</summary>
        Task CompleteAsync(int id);

        /// <summary>丢弃一项（→Dropped）。</summary>
        Task DropAsync(int id);

        /// <summary>查某目标的队列项。</summary>
        Task<List<ReviewQueueItemDto>> GetByTargetAsync(string targetRef, string targetKind);
    }

    /// <summary>队列项 DTO。</summary>
    public class ReviewQueueItemDto
    {
        public int Id { get; set; }
        public string TargetRef { get; set; } = "";
        public string TargetKind { get; set; } = "";
        public string? Reason { get; set; }
        public string Source { get; set; } = "";
        public string Priority { get; set; } = "Normal";
        public string Status { get; set; } = "Pending";
        public System.DateTime CreatedAt { get; set; }
    }
}
