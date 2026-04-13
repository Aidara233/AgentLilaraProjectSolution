using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace AgentCoreProcessor.Database
{
    /// <summary>
    /// 主记忆库数据访问。基于多维标签的查询，支持 Person 聚合。
    /// </summary>
    internal class MemoryRepository
    {
        private readonly DbManager db;

        public MemoryRepository(DbManager db) => this.db = db;

        /// <summary>
        /// 按多维标签并集过滤记忆。
        /// 返回 PersonId/ChannelId/TopicId 任一匹配 或 对应标签为 null（不限）的记忆。
        /// </summary>
        public async Task<List<MemoryEntry>> GetByTagsAsync(int? personId, int? channelId, int? topicId)
        {
            var all = await db.GetAllAsync<MemoryEntry>();
            var now = DateTime.Now;

            return all.FindAll(m =>
            {
                // 过滤过期临时记忆
                if (!m.IsPersistent && m.ExpiresAt != null && m.ExpiresAt < now)
                    return false;

                // 标签匹配：记忆标签为 null 表示不限，或与当前场景匹配
                bool personMatch = m.PersonId == null || m.PersonId == personId;
                bool channelMatch = m.ChannelId == null || m.ChannelId == channelId;
                bool topicMatch = m.TopicId == null || m.TopicId == topicId;

                // 至少有一个标签是具体匹配的（避免全 null 的记忆被所有场景命中）
                // 全 null 视为全局记忆，始终命中
                bool isGlobal = m.PersonId == null && m.ChannelId == null && m.TopicId == null;

                return (personMatch && channelMatch && topicMatch) &&
                       (isGlobal || m.PersonId == personId || m.ChannelId == channelId || m.TopicId == topicId);
            });
        }

        /// <summary>
        /// 获取指定自然人的所有记忆（聚合其名下所有 User 的记忆）。
        /// </summary>
        public async Task<List<MemoryEntry>> GetByPersonAsync(int personId)
        {
            var all = await db.Table<MemoryEntry>()
                .Where(m => m.PersonId == personId)
                .ToListAsync();
            return all;
        }

        /// <summary>按 CreatedAt 降序取最近 N 条。</summary>
        public async Task<List<MemoryEntry>> GetRecentAsync(int limit)
        {
            return await db.QueryAsync<MemoryEntry>(
                "SELECT * FROM Memories ORDER BY CreatedAt DESC LIMIT ?", limit);
        }

        /// <summary>批量按 ID 查询。</summary>
        public async Task<List<MemoryEntry>> GetByIdsAsync(List<int> ids)
        {
            if (ids.Count == 0) return new List<MemoryEntry>();
            var idList = string.Join(",", ids);
            return await db.QueryAsync<MemoryEntry>(
                $"SELECT * FROM Memories WHERE Id IN ({idList})");
        }

        /// <summary>创建一条主库记忆。</summary>
        public async Task<MemoryEntry> CreateAsync(
            string content, byte[]? embedding,
            int? personId = null, int? channelId = null, int? topicId = null,
            int? sourceMessageId = null, float importance = 0.5f)
        {
            var memory = new MemoryEntry
            {
                PersonId = personId,
                ChannelId = channelId,
                TopicId = topicId,
                Content = content,
                Embedding = embedding,
                Importance = importance,
                SourceMessageId = sourceMessageId,
                IsPersistent = true,
                CreatedAt = DateTime.Now,
                LastAccessedAt = DateTime.Now
            };
            await db.InsertAsync(memory);
            return memory;
        }

        /// <summary>更新记忆，同时刷新最后访问时间。</summary>
        public Task<int> UpdateAsync(MemoryEntry memory)
        {
            memory.LastAccessedAt = DateTime.Now;
            return db.UpdateAsync(memory);
        }

        /// <summary>删除一条记忆。</summary>
        public Task<int> DeleteAsync(MemoryEntry memory) => db.DeleteAsync(memory);

        public Task<MemoryEntry?> GetByIdAsync(int id) => db.GetByIdAsync<MemoryEntry>(id);

        // ---- 做梦相关查询 ----

        /// <summary>获取未被做梦处理的记忆（LastDreamTime 为空）。</summary>
        public Task<List<MemoryEntry>> GetUndreamedAsync(int limit)
        {
            return db.QueryAsync<MemoryEntry>(
                "SELECT * FROM Memories WHERE LastDreamTime IS NULL ORDER BY CreatedAt ASC LIMIT ?", limit);
        }

        /// <summary>获取最久未被做梦处理的记忆。</summary>
        public Task<List<MemoryEntry>> GetOldestDreamedAsync(int limit)
        {
            return db.QueryAsync<MemoryEntry>(
                "SELECT * FROM Memories WHERE LastDreamTime IS NOT NULL ORDER BY LastDreamTime ASC LIMIT ?", limit);
        }

        /// <summary>按 SourceHash 查询，用于衍生记忆防重复。</summary>
        public async Task<MemoryEntry?> GetBySourceHashAsync(string hash)
        {
            var results = await db.QueryAsync<MemoryEntry>(
                "SELECT * FROM Memories WHERE SourceHash = ? LIMIT 1", hash);
            return results.Count > 0 ? results[0] : null;
        }

        /// <summary>创建衍生记忆（做梦产生）。</summary>
        public async Task<MemoryEntry> CreateDerivedAsync(
            string content, byte[]? embedding,
            string sourceMemoryIds, string sourceHash,
            int? personId = null, int? channelId = null, int? topicId = null,
            float importance = 0.5f)
        {
            var memory = new MemoryEntry
            {
                PersonId = personId,
                ChannelId = channelId,
                TopicId = topicId,
                Content = content,
                Embedding = embedding,
                Importance = importance,
                IsDerived = true,
                SourceMemoryIds = sourceMemoryIds,
                SourceHash = sourceHash,
                IsPersistent = true,
                LastDreamTime = DateTime.Now,
                CreatedAt = DateTime.Now,
                LastAccessedAt = DateTime.Now
            };
            await db.InsertAsync(memory);
            return memory;
        }

        /// <summary>获取主库记忆总数。</summary>
        public async Task<int> GetCountAsync()
        {
            return await db.Table<MemoryEntry>().CountAsync();
        }
    }
}
