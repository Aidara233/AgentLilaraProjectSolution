using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AgentCoreProcessor.Engine.Mind;
using AgentLilara.PluginSDK.Services;

namespace AgentCoreProcessor.Tool.Host
{
    /// <summary>
    /// IReviewQueueAccess 桥接实现。映射 ReviewQueueService 到 SDK。
    /// </summary>
    internal class ReviewQueueAccessImpl : IReviewQueueAccess
    {
        private readonly ReviewQueueService _queue;

        public ReviewQueueAccessImpl(ReviewQueueService queue) => _queue = queue;

        public Task<int> EnqueueAsync(string targetRef, string targetKind, string reason, string source, string priority = "Normal")
            => _queue.EnqueueAsync(targetRef, targetKind, reason, source, priority);

        public async Task<List<ReviewQueueItemDto>> GetPendingAsync(int limit = 10)
        {
            var items = await _queue.GetPendingAsync(limit);
            return items.Select(ToDto).ToList();
        }

        public async Task<ReviewQueueItemDto?> ClaimAsync(int id)
        {
            var item = await _queue.ClaimAsync(id);
            return item != null ? ToDto(item) : null;
        }

        public Task CompleteAsync(int id) => _queue.CompleteAsync(id);

        public Task DropAsync(int id) => _queue.DropAsync(id);

        public async Task<List<ReviewQueueItemDto>> GetByTargetAsync(string targetRef, string targetKind)
        {
            var items = await _queue.GetByTargetAsync(targetRef, targetKind);
            return items.Select(ToDto).ToList();
        }

        private static ReviewQueueItemDto ToDto(Database.ReviewQueueItem item) => new()
        {
            Id = item.Id,
            TargetRef = item.TargetRef,
            TargetKind = item.TargetKind,
            Reason = item.Reason,
            Source = item.Source,
            Priority = item.Priority,
            Status = item.Status,
            CreatedAt = item.CreatedAt,
        };
    }
}
