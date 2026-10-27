using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace AgentCoreProcessor.Database
{
    /// <summary>
    /// 待验证队列数据访问。支持入队、查询、认领/完成/丢弃/中断恢复等状态流转。
    /// </summary>
    internal class ReviewQueueRepository
    {
        private readonly DbManager db;

        public ReviewQueueRepository(DbManager db) => this.db = db;

        /// <summary>入队一条待验证项。</summary>
        public async Task<ReviewQueueItem> CreateAsync(ReviewQueueItem item)
        {
            item.CreatedAt = DateTime.Now;
            await db.InsertAsync(item);
            return item;
        }

        /// <summary>按主键获取队列项，不存在时返回 null。</summary>
        public Task<ReviewQueueItem?> GetByIdAsync(int id)
            => db.GetByIdAsync<ReviewQueueItem>(id);

        /// <summary>
        /// 获取待处理队列项（Status=Pending）。
        /// 按优先级排序（High &gt; Normal &gt; Low），同级按创建时间升序（先入先出），取前 limit 条。
        /// </summary>
        public Task<List<ReviewQueueItem>> GetPendingAsync(int limit)
        {
            return db.QueryAsync<ReviewQueueItem>(
                "SELECT * FROM ReviewQueueItems WHERE Status = 'Pending' " +
                "ORDER BY CASE Priority WHEN 'High' THEN 0 WHEN 'Normal' THEN 1 WHEN 'Low' THEN 2 ELSE 3 END, " +
                "CreatedAt ASC LIMIT ?", limit);
        }

        /// <summary>获取所有处理中（Status=UnderReview）的队列项（崩溃恢复用）。</summary>
        public Task<List<ReviewQueueItem>> GetUnderReviewAsync()
        {
            return db.QueryAsync<ReviewQueueItem>(
                "SELECT * FROM ReviewQueueItems WHERE Status = 'UnderReview' ORDER BY ClaimedAt ASC");
        }

        /// <summary>获取全部队列项（按主键升序）。</summary>
        public Task<List<ReviewQueueItem>> GetAllAsync()
            => db.GetAllAsync<ReviewQueueItem>();

        /// <summary>认领队列项：置 Status=UnderReview + ClaimedAt=Now。</summary>
        public Task ClaimAsync(int id)
            => db.ExecuteAsync(
                "UPDATE ReviewQueueItems SET Status = 'UnderReview', ClaimedAt = ? WHERE Id = ?",
                DateTime.Now, id);

        /// <summary>完成队列项：置 Status=Done + DoneAt=Now。</summary>
        public Task CompleteAsync(int id)
            => db.ExecuteAsync(
                "UPDATE ReviewQueueItems SET Status = 'Done', DoneAt = ? WHERE Id = ?",
                DateTime.Now, id);

        /// <summary>丢弃队列项：置 Status=Dropped。</summary>
        public Task DropAsync(int id)
            => db.ExecuteAsync(
                "UPDATE ReviewQueueItems SET Status = 'Dropped' WHERE Id = ?", id);

        /// <summary>中断恢复：置 Status=Pending + InterruptedAt=Now（崩溃恢复）。</summary>
        public Task ResetInterruptedAsync(int id)
            => db.ExecuteAsync(
                "UPDATE ReviewQueueItems SET Status = 'Pending', InterruptedAt = ? WHERE Id = ?",
                DateTime.Now, id);

        /// <summary>按目标查询已有的队列项（目标引用 + 目标类型）。</summary>
        public Task<List<ReviewQueueItem>> GetByTargetAsync(string targetRef, string targetKind)
        {
            return db.QueryAsync<ReviewQueueItem>(
                "SELECT * FROM ReviewQueueItems WHERE TargetRef = ? AND TargetKind = ? ORDER BY CreatedAt ASC",
                targetRef, targetKind);
        }
    }
}
