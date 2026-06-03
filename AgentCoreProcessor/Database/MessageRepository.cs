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

        /// <summary>按数据库 ID 查找单条消息。</summary>
        public async Task<UserMessage?> GetByIdAsync(int messageId)
        {
            var results = await db.QueryAsync<UserMessage>(
                "SELECT * FROM UserMessages WHERE Id = ? LIMIT 1", messageId);
            return results.Count > 0 ? results[0] : null;
        }

        /// <summary>获取指定频道的最近 N 条消息（按频道级上下文）。</summary>
        public Task<List<UserMessage>> GetRecentByChannelAsync(int channelId, int limit)
        {
            return db.QueryAsync<UserMessage>(
                "SELECT * FROM UserMessages WHERE ChannelId = ? ORDER BY Time DESC LIMIT ?",
                channelId, limit);
        }

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

        /// <summary>获取指定频道中 Id > afterId 的最近 N 条消息（按 Id 降序取最新，再反转升序返回）。</summary>
        public async Task<List<UserMessage>> GetLatestAfterIdAsync(int channelId, int afterId, int limit = 20)
        {
            var results = await db.QueryAsync<UserMessage>(
                "SELECT * FROM UserMessages WHERE ChannelId = ? AND Id > ? ORDER BY Id DESC LIMIT ?",
                channelId, afterId, limit);
            results.Reverse();
            return results;
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
            var placeholders = string.Join(",", userIds.Select(_ => "?"));
            var result = await db.QueryAsync<CountResult>(
                $"SELECT COUNT(DISTINCT date(Time)) AS Value FROM UserMessages WHERE UserId IN ({placeholders})",
                userIds.Cast<object>().ToArray());
            return result.Count > 0 ? result[0].Value : 0;
        }

        /// <summary>获取某消息在指定频道内的排名（按 Id ASC，1-based）。</summary>
        public async Task<int?> GetMessageRankAsync(int channelId, int messageId)
        {
            var result = await db.QueryAsync<CountResult>(
                "SELECT COUNT(*) AS Value FROM UserMessages WHERE ChannelId = ? AND Id <= ?",
                channelId, messageId);
            return result.Count > 0 ? result[0].Value : null;
        }

        /// <summary>按排名（1-based，按 Id ASC）获取频道内某条消息的 db_id。</summary>
        public async Task<int?> GetMessageIdByRankAsync(int channelId, int rank)
        {
            var results = await db.QueryAsync<IdResult>(
                "SELECT Id FROM UserMessages WHERE ChannelId = ? ORDER BY Id ASC LIMIT 1 OFFSET ?",
                channelId, rank - 1);
            return results.Count > 0 ? results[0].Id : null;
        }

        /// <summary>
        /// 通用消息搜索。所有条件均为可选，至少提供一个有效条件。
        /// 结果按 Time DESC 排序，limit 最大 100。
        /// </summary>
        public Task<List<UserMessage>> SearchAsync(
            List<int>? channelIds,
            string? keyword,
            List<int>? userIds,
            DateTime? timeStart,
            DateTime? timeEnd,
            int limit = 20)
        {
            var sql = "SELECT * FROM UserMessages WHERE 1=1";
            var args = new List<object>();

            if (channelIds != null && channelIds.Count > 0)
            {
                var placeholders = string.Join(",", channelIds.Select(_ => "?"));
                sql += $" AND ChannelId IN ({placeholders})";
                args.AddRange(channelIds.Cast<object>());
            }

            if (userIds != null && userIds.Count > 0)
            {
                var placeholders = string.Join(",", userIds.Select(_ => "?"));
                sql += $" AND UserId IN ({placeholders})";
                args.AddRange(userIds.Cast<object>());
            }

            if (!string.IsNullOrEmpty(keyword))
            {
                sql += " AND (Content LIKE ? OR SenderName LIKE ?)";
                args.Add($"%{keyword}%");
                args.Add($"%{keyword}%");
            }

            if (timeStart != null)
            {
                sql += " AND Time >= ?";
                args.Add(timeStart.Value);
            }

            if (timeEnd != null)
            {
                sql += " AND Time <= ?";
                args.Add(timeEnd.Value);
            }

            limit = Math.Clamp(limit, 1, 100);
            sql += " ORDER BY Time DESC LIMIT ?";
            args.Add(limit);

            return db.QueryAsync<UserMessage>(sql, args.ToArray());
        }
    }

    internal class CountResult { public int Value { get; set; } }
    internal class IdResult { public int Id { get; set; } }
}