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
            int? personId = null, int? channelId = null, int? topicId = null,
            int? sourceMessageId = null)
        {
            var entry = new TempMemoryEntry
            {
                PersonId = personId,
                ChannelId = channelId,
                TopicId = topicId,
                Content = content,
                Embedding = embedding,
                SourceMessageId = sourceMessageId,
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

        /// <summary>按多维标签过滤临时记忆。</summary>
        public async Task<List<TempMemoryEntry>> GetByTagsAsync(int? personId, int? channelId, int? topicId)
        {
            var all = await GetAllAsync();
            return all.FindAll(m =>
            {
                bool personMatch = m.PersonId == null || m.PersonId == personId;
                bool channelMatch = m.ChannelId == null || m.ChannelId == channelId;
                bool topicMatch = m.TopicId == null || m.TopicId == topicId;
                bool isGlobal = m.PersonId == null && m.ChannelId == null && m.TopicId == null;

                return (personMatch && channelMatch && topicMatch) &&
                       (isGlobal || m.PersonId == personId || m.ChannelId == channelId || m.TopicId == topicId);
            });
        }

        /// <summary>删除一条临时记忆。</summary>
        public Task<int> DeleteAsync(TempMemoryEntry entry) => db.DeleteAsync(entry);

        /// <summary>清空全部临时记忆。</summary>
        public async Task<int> ClearAllAsync()
        {
            var all = await GetAllAsync();
            int count = 0;
            foreach (var entry in all)
                count += await db.DeleteAsync(entry);
            return count;
        }
    }
}
