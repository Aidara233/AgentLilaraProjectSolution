using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace AgentCoreProcessor.Database
{
    /// <summary>
    /// 消息数据访问，提供按频道查询历史消息、保存消息等操作。
    /// </summary>
    internal class MessageRepository
    {
        private readonly DbManager db;

        public MessageRepository(DbManager db) => this.db = db;

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

        /// <summary>获取指定频道的最近 N 条消息（按频道级上下文）。</summary>
        public Task<List<UserMessage>> GetRecentByChannelAsync(int channelId, int limit)
        {
            return db.QueryAsync<UserMessage>(
                "SELECT * FROM UserMessages WHERE ChannelId = ? ORDER BY Time DESC LIMIT ?",
                channelId, limit);
        }

        public Task<UserMessage?> GetByIdAsync(int id) => db.GetByIdAsync<UserMessage>(id);
    }
}
