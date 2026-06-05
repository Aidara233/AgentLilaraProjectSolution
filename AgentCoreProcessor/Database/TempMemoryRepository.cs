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
            string type = MemoryType.Fact, string? subject = null,
            string? embeddingModel = null)
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
                CreatedAt = DateTime.Now,
                EmbeddingModel = embeddingModel
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
                int matchCount = MemoryType.GetMatchScore(m.PersonId, m.ChannelId, m.Type, personId, channelId);

                results.Add((m, matchCount));
            }

            return results;
        }

        /// <summary>按 ID 获取临时记忆。</summary>
        public Task<TempMemoryEntry?> GetByIdAsync(int id) => db.GetByIdAsync<TempMemoryEntry>(id);

        /// <summary>获取临时记忆总数。</summary>
        public async Task<int> GetCountAsync()
        {
            var result = await db.QueryAsync<CountResult>(
                "SELECT COUNT(*) AS Value FROM TempMemories");
            return result.Count > 0 ? result[0].Value : 0;
        }

        /// <summary>删除一条临时记忆。</summary>
        public Task<int> DeleteAsync(TempMemoryEntry entry) => db.DeleteAsync(entry);

        /// <summary>更新一条临时记忆。</summary>
        public Task<int> UpdateAsync(TempMemoryEntry entry) => db.UpdateAsync(entry);

        /// <summary>清空全部临时记忆。</summary>
        public Task ClearAllAsync()
        {
            return db.ExecuteAsync("DELETE FROM TempMemories");
        }

        /// <summary>获取指定频道最近 N 条临时记忆（按时间降序）。</summary>
        public Task<List<TempMemoryEntry>> GetRecentByChannelAsync(int channelId, int limit = 10)
        {
            return db.QueryAsync<TempMemoryEntry>(
                "SELECT * FROM TempMemories WHERE ChannelId = ? ORDER BY CreatedAt DESC LIMIT ?",
                channelId, limit);
        }

        /// <summary>批量按 ID 查询。</summary>
        public async Task<List<TempMemoryEntry>> GetByIdsAsync(List<int> ids)
        {
            if (ids.Count == 0) return new List<TempMemoryEntry>();
            var placeholders = string.Join(",", ids.Select(_ => "?"));
            return await db.QueryAsync<TempMemoryEntry>(
                $"SELECT * FROM TempMemories WHERE Id IN ({placeholders})", ids.Cast<object>().ToArray());
        }

        /// <summary>批量更新热度。</summary>
        public async Task BatchUpdateHeatAsync(List<(int Id, float Heat)> updates)
        {
            if (updates.Count == 0) return;
            foreach (var (id, heat) in updates)
            {
                await db.ExecuteAsync(
                    "UPDATE TempMemories SET Heat = ? WHERE Id = ?",
                    heat, id);
            }
        }

        /// <summary>获取全部临时记忆的 Id 和 Content（用于重建 embedding）。</summary>
        public Task<List<StubEntry>> GetAllStubsAsync()
            => db.QueryAsync<StubEntry>("SELECT Id, Content FROM TempMemories");

        /// <summary>批量更新 embedding 和模型标记。</summary>
        public async Task BatchUpdateEmbeddingsAsync(List<(int Id, byte[] Embedding, string Model)> updates)
        {
            if (updates.Count == 0) return;
            foreach (var (id, embedding, model) in updates)
            {
                await db.ExecuteAsync(
                    "UPDATE TempMemories SET Embedding = ?, EmbeddingModel = ? WHERE Id = ?",
                    embedding, model, id);
            }
        }
    }
}
