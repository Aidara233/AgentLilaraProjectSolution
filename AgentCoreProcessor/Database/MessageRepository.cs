using System.Collections.Generic;
using System.Threading.Tasks;

namespace AgentCoreProcessor.Database
{
    /// <summary>
    /// 消息数据访问，提供按话题查询历史消息、保存消息等操作。
    /// </summary>
    internal class MessageRepository
    {
        private readonly DbManager db;

        public MessageRepository(DbManager db) => this.db = db;

        /// <summary>获取指定话题的所有消息，按时间升序。</summary>
        public Task<List<UserMessage>> GetByTopicAsync(int topicId)
        {
            return db.QueryAsync<UserMessage>(
                "SELECT * FROM UserMessages WHERE TopicId = ? ORDER BY Time ASC",
                topicId);
        }

        /// <summary>获取指定话题的最近 N 条消息（用于构建上下文窗口）。</summary>
        public Task<List<UserMessage>> GetRecentByTopicAsync(int topicId, int limit)
        {
            return db.QueryAsync<UserMessage>(
                "SELECT * FROM UserMessages WHERE TopicId = ? ORDER BY Time DESC LIMIT ?",
                topicId, limit);
        }

        /// <summary>保存一条用户消息。</summary>
        public async Task<UserMessage> SaveAsync(UserMessage message)
        {
            await db.InsertAsync(message);
            return message;
        }

        /// <summary>获取指定频道的所有消息，按时间升序。</summary>
        public Task<List<UserMessage>> GetByChannelAsync(int channelId)
        {
            return db.QueryAsync<UserMessage>(
                "SELECT * FROM UserMessages WHERE ChannelId = ? ORDER BY Time ASC",
                channelId);
        }

        public Task<UserMessage?> GetByIdAsync(int id) => db.GetByIdAsync<UserMessage>(id);
    }
}
