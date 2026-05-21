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

        /// <summary>按平台消息ID查找（用于引用消息上下文）。</summary>
        public async Task<UserMessage?> GetByPlatformMessageIdAsync(int channelId, string platformMessageId)
        {
            var results = await db.QueryAsync<UserMessage>(
                "SELECT * FROM UserMessages WHERE ChannelId = ? AND PlatformMessageId = ? LIMIT 1",
                channelId, platformMessageId);
            return results.Count > 0 ? results[0] : null;
        }

        /// <summary>以某条消息为锚点，取前后各 radius 条消息作为上下文。</summary>
        public async Task<List<UserMessage>> GetContextAroundAsync(int messageId, int channelId, int radius = 3)
        {
            var before = await db.QueryAsync<UserMessage>(
                "SELECT * FROM UserMessages WHERE ChannelId = ? AND Id < ? ORDER BY Id DESC LIMIT ?",
                channelId, messageId, radius);
            before.Reverse();

            var target = await db.QueryAsync<UserMessage>(
                "SELECT * FROM UserMessages WHERE Id = ?", messageId);

            var after = await db.QueryAsync<UserMessage>(
                "SELECT * FROM UserMessages WHERE ChannelId = ? AND Id > ? ORDER BY Id ASC LIMIT ?",
                channelId, messageId, radius);

            var result = new List<UserMessage>(before.Count + 1 + after.Count);
            result.AddRange(before);
            if (target.Count > 0) result.Add(target[0]);
            result.AddRange(after);
            return result;
        }

        /// <summary>分页查询频道消息，支持关键词搜索。</summary>
        public Task<List<UserMessage>> SearchByChannelAsync(int channelId, string? keyword, int offset, int limit)
        {
            if (string.IsNullOrEmpty(keyword))
            {
                return db.QueryAsync<UserMessage>(
                    "SELECT * FROM UserMessages WHERE ChannelId = ? ORDER BY Time DESC LIMIT ? OFFSET ?",
                    channelId, limit, offset);
            }
            return db.QueryAsync<UserMessage>(
                "SELECT * FROM UserMessages WHERE ChannelId = ? AND Content LIKE ? ORDER BY Time DESC LIMIT ? OFFSET ?",
                channelId, $"%{keyword}%", limit, offset);
        }

        /// <summary>获取频道消息总数。</summary>
        public async Task<int> GetCountByChannelAsync(int channelId)
        {
            var result = await db.QueryAsync<CountResult>(
                "SELECT COUNT(*) AS Value FROM UserMessages WHERE ChannelId = ?", channelId);
            return result.Count > 0 ? result[0].Value : 0;
        }

        /// <summary>获取指定频道中 Id <= upToId 的消息数量。</summary>
        public async Task<int> GetCountUpToAsync(int channelId, int upToId)
        {
            var result = await db.QueryAsync<CountResult>(
                "SELECT COUNT(*) AS Value FROM UserMessages WHERE ChannelId = ? AND Id <= ?",
                channelId, upToId);
            return result.Count > 0 ? result[0].Value : 0;
        }

        /// <summary>获取指定频道中 Id > afterId 的消息（按 Id 升序，最多 limit 条）。</summary>
        public Task<List<UserMessage>> GetAfterIdAsync(int channelId, int afterId, int limit = 50)
        {
            return db.QueryAsync<UserMessage>(
                "SELECT * FROM UserMessages WHERE ChannelId = ? AND Id > ? ORDER BY Id ASC LIMIT ?",
                channelId, afterId, limit);
        }

        /// <summary>获取指定频道中 Id <= beforeId 的最近 N 条消息（按 Id 升序返回）。</summary>
        public async Task<List<UserMessage>> GetBeforeIdAsync(int channelId, int beforeId, int limit = 10)
        {
            var results = await db.QueryAsync<UserMessage>(
                "SELECT * FROM UserMessages WHERE ChannelId = ? AND Id <= ? ORDER BY Id DESC LIMIT ?",
                channelId, beforeId, limit);
            results.Reverse();
            return results;
        }

        public async Task<int> GetCountByUserAsync(int userId)
        {
            var result = await db.QueryAsync<CountResult>(
                "SELECT COUNT(*) AS Value FROM UserMessages WHERE UserId = ?", userId);
            return result.Count > 0 ? result[0].Value : 0;
        }

        public async Task<int> GetDistinctDaysByUsersAsync(List<int> userIds)
        {
            if (userIds.Count == 0) return 0;
            var ids = string.Join(",", userIds);
            var result = await db.QueryAsync<CountResult>(
                $"SELECT COUNT(DISTINCT date(Time)) AS Value FROM UserMessages WHERE UserId IN ({ids})");
            return result.Count > 0 ? result[0].Value : 0;
        }
    }

    internal class CountResult { public int Value { get; set; } }
}