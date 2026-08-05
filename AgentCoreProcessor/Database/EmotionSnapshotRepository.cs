using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace AgentCoreProcessor.Database
{
    /// <summary>
    /// 情绪快照数据访问。支持创建、按主键查询、最新一条（重启恢复）和按时间倒序取最近 N 条。
    /// </summary>
    internal class EmotionSnapshotRepository
    {
        private readonly DbManager db;

        public EmotionSnapshotRepository(DbManager db) => this.db = db;

        /// <summary>创建一条情绪快照。</summary>
        public async Task<EmotionSnapshot> CreateAsync(EmotionSnapshot snapshot)
        {
            snapshot.CreatedAt = DateTime.Now;
            await db.InsertAsync(snapshot);
            return snapshot;
        }

        /// <summary>按主键获取单条快照，不存在时返回 null。</summary>
        public Task<EmotionSnapshot?> GetByIdAsync(int id)
            => db.GetByIdAsync<EmotionSnapshot>(id);

        /// <summary>获取最新一条情绪快照（重启恢复用）。</summary>
        public async Task<EmotionSnapshot?> GetLatestAsync()
        {
            var results = await db.QueryAsync<EmotionSnapshot>(
                "SELECT * FROM EmotionSnapshots ORDER BY CreatedAt DESC, Id DESC LIMIT 1");
            return results.Count > 0 ? results[0] : null;
        }

        /// <summary>按时间倒序取最近 N 条情绪快照。</summary>
        public Task<List<EmotionSnapshot>> GetRecentAsync(int limit)
        {
            return db.QueryAsync<EmotionSnapshot>(
                "SELECT * FROM EmotionSnapshots ORDER BY CreatedAt DESC, Id DESC LIMIT ?", limit);
        }

        /// <summary>获取全部情绪快照（按主键升序）。</summary>
        public Task<List<EmotionSnapshot>> GetAllAsync()
            => db.GetAllAsync<EmotionSnapshot>();
    }
}
