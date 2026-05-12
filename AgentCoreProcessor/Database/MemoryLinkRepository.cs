using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace AgentCoreProcessor.Database
{
    /// <summary>
    /// 记忆关联表数据访问。做梦时由"关联重建"片段维护，检索时用于关联扩展。
    /// </summary>
    internal class MemoryLinkRepository
    {
        private readonly DbManager db;

        public MemoryLinkRepository(DbManager db) => this.db = db;

        /// <summary>
        /// 一次查出多条记忆的所有强关联。用于检索时的关联扩展。
        /// </summary>
        public async Task<List<MemoryLink>> GetLinksForAsync(List<int> memoryIds, float minStrength = 0.5f)
        {
            if (memoryIds.Count == 0) return new List<MemoryLink>();
            var idList = string.Join(",", memoryIds);
            var links = await db.QueryAsync<MemoryLink>(
                $"SELECT * FROM MemoryLinks WHERE (SourceId IN ({idList}) OR TargetId IN ({idList})) AND Strength >= ?",
                minStrength);
            return links;
        }

        /// <summary>创建或更新关联。已存在则更新 Strength 和 UpdatedAt。</summary>
        public async Task<MemoryLink> CreateOrUpdateAsync(int sourceId, int targetId, float strength, string linkType)
        {
            // 查找已有关联（双向）
            var existing = await db.QueryAsync<MemoryLink>(
                "SELECT * FROM MemoryLinks WHERE (SourceId = ? AND TargetId = ?) OR (SourceId = ? AND TargetId = ?)",
                sourceId, targetId, targetId, sourceId);

            if (existing.Count > 0)
            {
                var link = existing[0];
                link.Strength = strength;
                link.LinkType = linkType;
                link.UpdatedAt = DateTime.Now;
                await db.UpdateAsync(link);
                return link;
            }

            var newLink = new MemoryLink
            {
                SourceId = sourceId,
                TargetId = targetId,
                Strength = strength,
                LinkType = linkType,
                CreatedAt = DateTime.Now,
                UpdatedAt = DateTime.Now
            };
            await db.InsertAsync(newLink);
            return newLink;
        }

        /// <summary>删除一条关联。</summary>
        public Task<int> DeleteAsync(MemoryLink link) => db.DeleteAsync(link);

        /// <summary>删除指向不存在记忆的孤立关联。</summary>
        public async Task<int> DeleteOrphanedAsync()
        {
            var orphaned = await db.QueryAsync<MemoryLink>(
                @"SELECT ml.* FROM MemoryLinks ml
                  WHERE NOT EXISTS (SELECT 1 FROM Memories WHERE Id = ml.SourceId)
                     OR NOT EXISTS (SELECT 1 FROM Memories WHERE Id = ml.TargetId)");
            int count = 0;
            foreach (var link in orphaned)
            {
                await db.DeleteAsync(link);
                count++;
            }
            return count;
        }

        /// <summary>删除指定记忆的所有关联。</summary>
        public async Task DeleteOrphanedForMemoryAsync(int memoryId)
        {
            await db.ExecuteAsync(
                "DELETE FROM MemoryLinks WHERE SourceId = ? OR TargetId = ?",
                memoryId, memoryId);
        }

        /// <summary>批量按 ID 删除关联。</summary>
        public async Task DeleteByIdsAsync(List<int> ids)
        {
            if (ids.Count == 0) return;
            var idList = string.Join(",", ids);
            await db.ExecuteAsync($"DELETE FROM MemoryLinks WHERE Id IN ({idList})");
        }

        /// <summary>获取指定记忆的所有关联。</summary>
        public async Task<List<MemoryLink>> GetByMemoryIdAsync(int memoryId)
        {
            return await db.QueryAsync<MemoryLink>(
                "SELECT * FROM MemoryLinks WHERE SourceId = ? OR TargetId = ?",
                memoryId, memoryId);
        }

        /// <summary>获取全部关联（供图谱页使用）。</summary>
        public Task<List<MemoryLink>> GetAllLinksAsync()
            => db.GetAllAsync<MemoryLink>();
    }
}
