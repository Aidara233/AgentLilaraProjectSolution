using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AgentCoreProcessor.Database;

namespace AgentCoreProcessor.Memory
{
    /// <summary>
    /// 记忆服务，封装记忆的存取、检索和清理逻辑。
    /// 起步阶段使用关键词匹配检索，后续接入向量模型做语义检索。
    /// </summary>
    internal class MemoryService
    {
        private readonly MemoryRepository memories;

        public MemoryService(MemoryRepository memories)
        {
            this.memories = memories;
        }

        /// <summary>写入一条记忆。</summary>
        public Task<MemoryEntry> StoreAsync(MemoryScope scope, int scopeId, string content,
            bool isPersistent = true, TimeSpan? ttl = null)
        {
            return memories.CreateAsync(scope, scopeId, content, isPersistent, ttl);
        }

        /// <summary>
        /// 按作用域召回相关记忆。
        /// 收集 Global + User + Channel + Topic 四个维度的记忆，
        /// 可选用关键词过滤，按最近访问时间排序取 topK。
        /// </summary>
        public async Task<List<MemoryEntry>> RecallAsync(int userId, int channelId, int topicId,
            string? query = null, int topK = 10)
        {
            // 并行收集四个作用域的记忆
            var globalTask = memories.GetGlobalAsync();
            var userTask = memories.GetByUserAsync(userId);
            var channelTask = memories.GetByChannelAsync(channelId);
            var topicTask = memories.GetByTopicAsync(topicId);

            await Task.WhenAll(globalTask, userTask, channelTask, topicTask);

            var all = new List<MemoryEntry>();
            all.AddRange(globalTask.Result);
            all.AddRange(userTask.Result);
            all.AddRange(channelTask.Result);
            all.AddRange(topicTask.Result);

            // 关键词过滤（起步阶段的简易检索，后续替换为向量语义匹配）
            if (!string.IsNullOrWhiteSpace(query))
            {
                var keywords = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                all = all.FindAll(m =>
                    keywords.Any(k => m.Content.Contains(k, StringComparison.OrdinalIgnoreCase)));
            }

            // 按最近访问时间降序，取 topK
            var result = all
                .OrderByDescending(m => m.LastAccessedAt)
                .Take(topK)
                .ToList();

            // 更新被召回记忆的 LastAccessedAt（LRU）
            foreach (var m in result)
                await memories.UpdateAsync(m);

            return result;
        }

        /// <summary>删除指定记忆。</summary>
        public async Task ForgetAsync(int memoryId)
        {
            var memory = await memories.GetByIdAsync(memoryId);
            if (memory != null)
                await memories.DeleteAsync(memory);
        }

        /// <summary>清理过期的临时记忆，返回删除数量。</summary>
        public Task<int> CleanupAsync()
        {
            return memories.CleanExpiredAsync();
        }
    }
}