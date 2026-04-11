using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace AgentCoreProcessor.Database
{
    /// <summary>
    /// 记忆数据访问，提供按作用域检索、创建、过期清理等操作。
    /// </summary>
    internal class MemoryRepository
    {
        private readonly DbManager db;

        public MemoryRepository(DbManager db) => this.db = db;

        /// <summary>按作用域和作用域ID检索所有有效记忆（排除已过期的临时记忆）。</summary>
        public async Task<List<MemoryEntry>> GetByScopeAsync(MemoryScope scope, int scopeId)
        {
            var now = DateTime.Now;
            var all = await db.Table<MemoryEntry>()
                .Where(m => m.Scope == scope && m.ScopeId == scopeId)
                .ToListAsync();

            // 过滤掉已过期的临时记忆
            return all.FindAll(m => m.IsPersistent || m.ExpiresAt == null || m.ExpiresAt > now);
        }

        /// <summary>获取全局记忆。</summary>
        public Task<List<MemoryEntry>> GetGlobalAsync()
        {
            return GetByScopeAsync(MemoryScope.Global, 0);
        }

        /// <summary>获取指定用户的记忆。</summary>
        public Task<List<MemoryEntry>> GetByUserAsync(int userId)
        {
            return GetByScopeAsync(MemoryScope.User, userId);
        }

        /// <summary>获取指定频道的记忆。</summary>
        public Task<List<MemoryEntry>> GetByChannelAsync(int channelId)
        {
            return GetByScopeAsync(MemoryScope.Channel, channelId);
        }

        /// <summary>获取指定话题的记忆。</summary>
        public Task<List<MemoryEntry>> GetByTopicAsync(int topicId)
        {
            return GetByScopeAsync(MemoryScope.Topic, topicId);
        }

        /// <summary>创建一条新记忆。</summary>
        public async Task<MemoryEntry> CreateAsync(MemoryScope scope, int scopeId, string content,
            bool isPersistent = true, TimeSpan? ttl = null)
        {
            var memory = new MemoryEntry
            {
                Scope = scope,
                ScopeId = scopeId,
                Content = content,
                IsPersistent = isPersistent,
                ExpiresAt = isPersistent ? null : DateTime.Now + ttl,
                CreatedAt = DateTime.Now,
                LastAccessedAt = DateTime.Now
            };
            await db.InsertAsync(memory);
            return memory;
        }

        /// <summary>更新记忆内容，同时刷新最后访问时间。</summary>
        public Task<int> UpdateAsync(MemoryEntry memory)
        {
            memory.LastAccessedAt = DateTime.Now;
            return db.UpdateAsync(memory);
        }

        /// <summary>删除一条记忆。</summary>
        public Task<int> DeleteAsync(MemoryEntry memory) => db.DeleteAsync(memory);

        /// <summary>清理所有已过期的临时记忆，返回删除数量。</summary>
        public async Task<int> CleanExpiredAsync()
        {
            var now = DateTime.Now;
            var expired = await db.Table<MemoryEntry>()
                .Where(m => !m.IsPersistent && m.ExpiresAt != null)
                .ToListAsync();

            // sqlite-net 的 Where 不支持 DateTime 比较，需要在内存中过滤
            expired = expired.FindAll(m => m.ExpiresAt < now);

            var count = 0;
            foreach (var m in expired)
                count += await db.DeleteAsync(m);
            return count;
        }

        public Task<MemoryEntry?> GetByIdAsync(int id) => db.GetByIdAsync<MemoryEntry>(id);
    }
}
