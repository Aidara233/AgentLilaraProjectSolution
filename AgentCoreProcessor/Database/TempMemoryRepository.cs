using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace AgentCoreProcessor.Database
{
    /// <summary>
    /// 临时记忆库数据访问。物理独立于主库，小而快。
    /// </summary>
    internal class TempMemoryRepository
    {
        private readonly DbManager db;

        public TempMemoryRepository(DbManager db) => this.db = db;

        /// <summary>写入一条临时记忆。</summary>
        public async Task<TempMemoryEntry> CreateAsync(
            string content, byte[]? embedding,
            int? personId = null, int? channelId = null,
            int? sourceMessageId = null, string confidence = "high",
            string type = MemoryType.Fact, string? subject = null)
        {
            var entry = new TempMemoryEntry
            {
                PersonId = personId,
                ChannelId = channelId,
                Content = content,
                Embedding = embedding,
                SourceMessageId = sourceMessageId,
                Confidence = confidence,
                Type = type,
                Subject = subject,
                CreatedAt = DateTime.Now
            };
            await db.InsertAsync(entry);
            return entry;
        }

        /// <summary>获取全部临时记忆（临时库小，可全量读）。</summary>
        public Task<List<TempMemoryEntry>> GetAllAsync()
        {
            return db.GetAllAsync<TempMemoryEntry>();
        }

        /// <summary>
        /// 获取全部临时记忆并计算标签匹配分。
        /// 不做硬过滤：所有记忆都返回，person/channel 匹配作为加分项。
        /// </summary>
        public async Task<List<(TempMemoryEntry Entry, int MatchCount)>> GetAllWithMatchScoreAsync(
            int? personId, int? channelId)
        {
            var all = await GetAllAsync();
            var results = new List<(TempMemoryEntry, int)>();

            foreach (var m in all)
            {
                int matchCount = 0;
                if (m.PersonId != null && m.PersonId == personId) matchCount++;
                if (m.ChannelId != null && m.ChannelId == channelId) matchCount++;
                if (m.Type == MemoryType.Knowledge) matchCount++;

                results.Add((m, matchCount));
            }

            return results;
        }

        /// <summary>删除一条临时记忆。</summary>
        public Task<int> DeleteAsync(TempMemoryEntry entry) => db.DeleteAsync(entry);

        /// <summary>更新一条临时记忆。</summary>
        public Task<int> UpdateAsync(TempMemoryEntry entry) => db.UpdateAsync(entry);

        /// <summary>清空全部临时记忆。</summary>
        public async Task<int> ClearAllAsync()
        {
            var all = await GetAllAsync();
            int count = 0;
            foreach (var entry in all)
                count += await db.DeleteAsync(entry);
            return count;
        }

        /// <summary>获取指定频道最近 N 条临时记忆（按时间降序）。</summary>
        public Task<List<TempMemoryEntry>> GetRecentByChannelAsync(int channelId, int limit = 10)
        {
            return db.QueryAsync<TempMemoryEntry>(
                "SELECT * FROM TempMemoryEntry WHERE ChannelId = ? ORDER BY CreatedAt DESC LIMIT ?",
                channelId, limit);
        }
    }
}
