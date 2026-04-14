using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace AgentCoreProcessor.Database
{
    /// <summary>
    /// 话题数据访问，提供按频道查询活跃话题、创建话题等操作。
    /// </summary>
    internal class TopicRepository
    {
        private readonly DbManager db;

        public TopicRepository(DbManager db) => this.db = db;

        /// <summary>获取指定频道内所有活跃话题。</summary>
        public Task<List<Topic>> GetActiveByChannelAsync(int channelId)
        {
            return db.Table<Topic>()
                .Where(t => t.ChannelId == channelId && t.IsActive)
                .ToListAsync();
        }

        /// <summary>创建新话题。</summary>
        public async Task<Topic> CreateAsync(int channelId, string name, string summary = "")
        {
            var topic = new Topic
            {
                ChannelId = channelId,
                Name = name,
                Summary = summary,
                IsActive = true,
                LastMessageTime = DateTime.Now
            };
            await db.InsertAsync(topic);
            return topic;
        }

        /// <summary>
        /// 将超过指定时间未活跃的话题标记为关闭。闲聊话题不过期。
        /// </summary>
        public async Task<int> DeactivateStaleAsync(TimeSpan timeout)
        {
            var cutoff = DateTime.Now - timeout;
            var stale = await db.Table<Topic>()
                .Where(t => t.IsActive && !t.IsChatTopic && t.LastMessageTime < cutoff)
                .ToListAsync();

            foreach (var t in stale)
                t.IsActive = false;

            var count = 0;
            foreach (var t in stale)
                count += await db.UpdateAsync(t);
            return count;
        }

        /// <summary>获取指定频道内所有有 embedding 的活跃话题。</summary>
        public Task<List<Topic>> GetActiveWithEmbeddingAsync(int channelId)
        {
            return db.QueryAsync<Topic>(
                "SELECT * FROM Topics WHERE ChannelId = ? AND IsActive = 1 AND Embedding IS NOT NULL",
                channelId);
        }

        /// <summary>获取指定频道的闲聊兜底话题，没有则返回 null。</summary>
        public async Task<Topic?> GetChatTopicAsync(int channelId)
        {
            var list = await db.Table<Topic>()
                .Where(t => t.ChannelId == channelId && t.IsChatTopic)
                .ToListAsync();
            return list.FirstOrDefault();
        }

        public Task<Topic?> GetByIdAsync(int id) => db.GetByIdAsync<Topic>(id);

        public Task<int> UpdateAsync(Topic topic) => db.UpdateAsync(topic);
    }
}
