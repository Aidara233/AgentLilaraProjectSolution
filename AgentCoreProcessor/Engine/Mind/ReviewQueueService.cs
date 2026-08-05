using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AgentCoreProcessor.Database;

namespace AgentCoreProcessor.Engine.Mind
{
    /// <summary>
    /// 待验证队列服务。封装 ReviewQueueRepository 的入队/认领/完成/丢弃/崩溃恢复等状态流转，
    /// 作为各来源（冲突组超预算 / Dream 发现 / 手动标记 / 框架事件）统一入队 + 认领处理的服务层。
    /// 由 ReviewEngine / MindEngine 等持有使用。
    /// </summary>
    internal class ReviewQueueService
    {
        /// <summary>合法优先级集合（其余一律按默认 Normal 处理）。</summary>
        private static readonly HashSet<string> ValidPriorities = new()
        {
            "High", "Normal", "Low",
        };

        private readonly ReviewQueueRepository _repo;

        public ReviewQueueService(ReviewQueueRepository repo) => _repo = repo;

        /// <summary>
        /// 入队一条待验证项。若已存在 Pending / UnderReview 的同一目标项则跳过（去重），返回已存在项的 Id。
        /// Priority 非法时回退默认 Normal。成功入队返回新项 Id。
        /// </summary>
        public async Task<int> EnqueueAsync(string targetRef, string targetKind, string reason, string source, string priority = "Normal")
        {
            // 去重：同一目标已有待处理项则跳过，返回现有项 Id
            var existing = await _repo.GetByTargetAsync(targetRef, targetKind);
            if (existing.Any(i => i.Status == "Pending" || i.Status == "UnderReview"))
                return existing.First(i => i.Status == "Pending" || i.Status == "UnderReview").Id;

            // 优先级校验：非法值回退默认 Normal
            if (!ValidPriorities.Contains(priority))
                priority = "Normal";

            var item = new ReviewQueueItem
            {
                TargetRef = targetRef,
                TargetKind = targetKind,
                Reason = reason,
                Source = source,
                Priority = priority,
                Status = "Pending",
            };

            var created = await _repo.CreateAsync(item);
            return created.Id;
        }

        /// <summary>获取待认领项（按 Priority High &gt; Normal &gt; Low + CreatedAt ASC，仓库已排序）。</summary>
        public Task<List<ReviewQueueItem>> GetPendingAsync(int limit = 10)
            => _repo.GetPendingAsync(limit);

        /// <summary>
        /// 认领单个队列项：Pending → UnderReview + ClaimedAt=Now。
        /// 认领成功返回该项；项不存在或已被认领（非 Pending）返回 null。
        /// </summary>
        public async Task<ReviewQueueItem?> ClaimAsync(int id)
        {
            var item = await _repo.GetByIdAsync(id);
            if (item == null || item.Status != "Pending")
                return null;

            await _repo.ClaimAsync(id);
            item.Status = "UnderReview";
            item.ClaimedAt = DateTime.Now;
            return item;
        }

        /// <summary>完成队列项：→ Done + DoneAt=Now。</summary>
        public Task CompleteAsync(int id)
            => _repo.CompleteAsync(id);

        /// <summary>丢弃队列项：→ Dropped。</summary>
        public Task DropAsync(int id)
            => _repo.DropAsync(id);

        /// <summary>
        /// 崩溃恢复：把所有 UnderReview 的项重置回 Pending + InterruptedAt=Now。
        /// 返回重置数量。
        /// </summary>
        public async Task<int> ResetInterruptedAsync()
        {
            var underReview = await _repo.GetUnderReviewAsync();
            foreach (var item in underReview)
                await _repo.ResetInterruptedAsync(item.Id);
            return underReview.Count;
        }

        /// <summary>按目标查询队列项（目标引用 + 目标类型）。</summary>
        public Task<List<ReviewQueueItem>> GetByTargetAsync(string targetRef, string targetKind)
            => _repo.GetByTargetAsync(targetRef, targetKind);

        /// <summary>是否有待处理项（GetPendingAsync(1) 非空）。</summary>
        public async Task<bool> HasPendingAsync()
        {
            var pending = await _repo.GetPendingAsync(1);
            return pending.Count > 0;
        }
    }
}
