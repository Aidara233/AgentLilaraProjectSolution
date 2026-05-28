using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AgentCoreProcessor.Client;
using AgentCoreProcessor.Database;
using AgentCoreProcessor.Util;
using AgentLilara.PluginSDK.Services;

namespace AgentCoreProcessor.Tool.Host
{
    /// <summary>
    /// IMemoryAccess 桥接实现。将 SDK 接口映射到内部 Repository + EmbeddingProvider。
    /// </summary>
    internal class MemoryAccessImpl : IMemoryAccess
    {
        private readonly MemoryRepository memories;
        private readonly TempMemoryRepository tempMemories;
        private readonly MemoryLinkRepository links;
        private readonly IEmbeddingProvider embedding;

        public MemoryAccessImpl(
            MemoryRepository memories,
            TempMemoryRepository tempMemories,
            MemoryLinkRepository links,
            IEmbeddingProvider embedding)
        {
            this.memories = memories;
            this.tempMemories = tempMemories;
            this.links = links;
            this.embedding = embedding;
        }

        // ===== 主记忆库 =====

        public async Task<int> StoreAsync(MemoryWriteRequest request)
        {
            var vec = await embedding.GetEmbeddingAsync(request.Content);
            var entry = await memories.CreateAsync(
                request.Content,
                VectorUtil.FloatsToBytes(vec),
                personId: request.PersonId,
                channelId: request.ChannelId,
                importance: request.Importance,
                confidence: request.Confidence,
                type: request.Type ?? Database.MemoryType.Fact,
                subject: request.Subject);
            if (!request.IsPersistent || request.ExpiresAt != null)
            {
                entry.IsPersistent = request.IsPersistent;
                entry.ExpiresAt = request.ExpiresAt;
                await memories.UpdateAsync(entry);
            }
            return entry.Id;
        }

        public async Task<List<AgentLilara.PluginSDK.Services.MemoryEntry>> SemanticSearchAsync(
            string query, int limit = 20, int? personId = null, int? channelId = null)
        {
            var vec = await embedding.GetEmbeddingAsync(query);
            var vecBytes = VectorUtil.FloatsToBytes(vec);
            var all = await memories.GetAllWithMatchScoreAsync(personId, channelId);

            return all
                .Where(x => x.Entry.Embedding != null)
                .Select(x => (x.Entry, x.MatchCount,
                    Sim: VectorUtil.CosineSimilarity(vec, VectorUtil.BytesToFloats(x.Entry.Embedding!))))
                .OrderByDescending(x => x.Sim)
                .Take(limit)
                .Select(x => ToSdkEntry(x.Entry, x.Sim))
                .ToList();
        }

        public async Task<List<AgentLilara.PluginSDK.Services.MemoryEntry>> FilterAsync(MemoryFilter filter)
        {
            var all = await memories.GetAllWithMatchScoreAsync(filter.PersonId, filter.ChannelId);
            IEnumerable<Database.MemoryEntry> query = all.Select(x => x.Entry);

            if (filter.PersonId != null)
                query = query.Where(m => m.PersonId == filter.PersonId);
            if (filter.ChannelId != null)
                query = query.Where(m => m.ChannelId == filter.ChannelId);
            if (filter.Type != null)
                query = query.Where(m => m.Type == filter.Type);
            if (filter.Subject != null)
                query = query.Where(m => m.Subject != null && m.Subject.Contains(filter.Subject, StringComparison.OrdinalIgnoreCase));
            if (filter.KeywordContains != null)
                query = query.Where(m => m.Content.Contains(filter.KeywordContains, StringComparison.OrdinalIgnoreCase));
            if (filter.CreatedAfter != null)
                query = query.Where(m => m.CreatedAt >= filter.CreatedAfter);
            if (filter.CreatedBefore != null)
                query = query.Where(m => m.CreatedAt <= filter.CreatedBefore);
            if (filter.MinImportance != null)
                query = query.Where(m => m.Importance >= filter.MinImportance);

            return query
                .Skip(filter.Offset)
                .Take(filter.Limit)
                .Select(m => ToSdkEntry(m, 0))
                .ToList();
        }

        public async Task<List<AgentLilara.PluginSDK.Services.MemoryEntry>> ListAsync(int offset = 0, int limit = 100)
        {
            var all = await memories.GetAllWithMatchScoreAsync(null, null);
            return all
                .Select(x => x.Entry)
                .OrderByDescending(m => m.CreatedAt)
                .Skip(offset)
                .Take(limit)
                .Select(m => ToSdkEntry(m, 0))
                .ToList();
        }

        public async Task<int> CountAsync()
        {
            return await memories.GetCountAsync();
        }

        public async Task<AgentLilara.PluginSDK.Services.MemoryEntry?> GetByIdAsync(int id)
        {
            var entry = await memories.GetByIdAsync(id);
            return entry == null ? null : ToSdkEntry(entry, 0);
        }

        public async Task DeleteAsync(int id)
        {
            var entry = await memories.GetByIdAsync(id);
            if (entry != null) await memories.DeleteAsync(entry);
        }

        public async Task UpdateAsync(int id, string newContent)
        {
            var entry = await memories.GetByIdAsync(id);
            if (entry == null) return;
            entry.Content = newContent;
            var vec = await embedding.GetEmbeddingAsync(newContent);
            entry.Embedding = VectorUtil.FloatsToBytes(vec);
            await memories.UpdateAsync(entry);
        }

        // ===== 关联图 =====

        public async Task<List<AgentLilara.PluginSDK.Services.LinkedMemoryEntry>> GetLinkedAsync(int memoryId)
        {
            var memLinks = await links.GetByMemoryIdAsync(memoryId);
            if (memLinks.Count == 0) return new();

            var linkedIds = memLinks
                .Select(l => l.SourceId == memoryId ? l.TargetId : l.SourceId)
                .Distinct()
                .ToList();
            var entries = await memories.GetByIdsAsync(linkedIds);
            var entryMap = entries.ToDictionary(e => e.Id);

            return memLinks.Select(link =>
            {
                var otherId = link.SourceId == memoryId ? link.TargetId : link.SourceId;
                if (!entryMap.TryGetValue(otherId, out var entry))
                    return null;

                return new AgentLilara.PluginSDK.Services.LinkedMemoryEntry
                {
                    LinkId = link.Id,
                    MemoryId = otherId,
                    Content = entry.Content,
                    Type = entry.Type,
                    Subject = entry.Subject,
                    PersonId = entry.PersonId,
                    ChannelId = entry.ChannelId,
                    Importance = entry.Importance,
                    Confidence = entry.Confidence,
                    Strength = link.Strength,
                    LinkType = link.LinkType,
                    LinkedAt = link.CreatedAt
                };
            }).Where(x => x != null).Cast<AgentLilara.PluginSDK.Services.LinkedMemoryEntry>().ToList();
        }

        public async Task LinkAsync(int fromId, int toId, float strength = 1.0f, string linkType = "semantic")
        {
            await links.CreateOrUpdateAsync(fromId, toId, strength, linkType);
        }

        public async Task UnlinkAsync(int fromId, int toId)
        {
            var memLinks = await links.GetByMemoryIdAsync(fromId);
            var link = memLinks.FirstOrDefault(l =>
                (l.SourceId == fromId && l.TargetId == toId) ||
                (l.SourceId == toId && l.TargetId == fromId));
            if (link != null) await links.DeleteAsync(link);
        }

        // ===== 向量操作 =====

        public async Task<float[]?> GetEmbeddingAsync(int memoryId)
        {
            var entry = await memories.GetByIdAsync(memoryId);
            if (entry?.Embedding == null) return null;
            return VectorUtil.BytesToFloats(entry.Embedding);
        }

        public async Task<float[]> ComputeEmbeddingAsync(string text)
        {
            return await embedding.GetEmbeddingAsync(text);
        }

        public async Task<List<AgentLilara.PluginSDK.Services.MemoryEntry>> VectorSearchAsync(
            float[] vec, int limit = 20, int? personId = null, int? channelId = null)
        {
            var all = await memories.GetAllWithMatchScoreAsync(personId, channelId);
            return all
                .Where(x => x.Entry.Embedding != null)
                .Select(x => (x.Entry,
                    Sim: VectorUtil.CosineSimilarity(vec, VectorUtil.BytesToFloats(x.Entry.Embedding!))))
                .OrderByDescending(x => x.Sim)
                .Take(limit)
                .Select(x => ToSdkEntry(x.Entry, x.Sim))
                .ToList();
        }

        // ===== 临时记忆库 =====

        public async Task<int> StoreTempAsync(TempMemoryWriteRequest request)
        {
            var vec = await embedding.GetEmbeddingAsync(request.Content);
            var entry = await tempMemories.CreateAsync(
                request.Content,
                VectorUtil.FloatsToBytes(vec),
                personId: request.PersonId,
                channelId: request.ChannelId,
                type: request.Type ?? Database.MemoryType.Fact,
                subject: request.Subject);
            return entry.Id;
        }

        public async Task<List<AgentLilara.PluginSDK.Services.TempMemoryEntry>> SearchTempAsync(
            string query, int limit = 20)
        {
            var vec = await embedding.GetEmbeddingAsync(query);
            var all = await tempMemories.GetAllAsync();
            return all
                .Where(x => x.Embedding != null)
                .Select(x => (x, Sim: VectorUtil.CosineSimilarity(vec, VectorUtil.BytesToFloats(x.Embedding!))))
                .OrderByDescending(x => x.Sim)
                .Take(limit)
                .Select(x => ToSdkTempEntry(x.x, x.Sim))
                .ToList();
        }

        public async Task<List<AgentLilara.PluginSDK.Services.TempMemoryEntry>> ListTempAsync(
            int offset = 0, int limit = 100)
        {
            var all = await tempMemories.GetAllAsync();
            return all
                .OrderByDescending(m => m.CreatedAt)
                .Skip(offset)
                .Take(limit)
                .Select(m => ToSdkTempEntry(m, 0))
                .ToList();
        }

        public async Task<int> CountTempAsync()
        {
            var result = await tempMemories.GetCountAsync();
            return result;
        }

        public async Task DeleteTempAsync(int id)
        {
            var entry = await tempMemories.GetByIdAsync(id);
            if (entry != null) await tempMemories.DeleteAsync(entry);
        }

        // ===== 映射 =====

        private static AgentLilara.PluginSDK.Services.MemoryEntry ToSdkEntry(Database.MemoryEntry m, float score)
        {
            return new AgentLilara.PluginSDK.Services.MemoryEntry
            {
                Id = m.Id,
                Content = m.Content,
                Type = m.Type,
                Subject = m.Subject,
                PersonId = m.PersonId,
                ChannelId = m.ChannelId,
                Importance = m.Importance,
                Confidence = m.Confidence,
                IsPersistent = m.IsPersistent,
                CreatedAt = m.CreatedAt,
                ExpiresAt = m.ExpiresAt,
                Score = score
            };
        }

        private static AgentLilara.PluginSDK.Services.TempMemoryEntry ToSdkTempEntry(
            Database.TempMemoryEntry m, float score)
        {
            return new AgentLilara.PluginSDK.Services.TempMemoryEntry
            {
                Id = m.Id,
                Content = m.Content,
                Type = m.Type,
                Subject = m.Subject,
                PersonId = m.PersonId,
                ChannelId = m.ChannelId,
                CreatedAt = m.CreatedAt,
                Score = score
            };
        }
    }
}
