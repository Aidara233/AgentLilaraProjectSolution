using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AgentCoreProcessor.Client;
using AgentCoreProcessor.Database;

namespace AgentCoreProcessor.Memory
{
    /// <summary>
    /// 记忆服务。双库架构（临时库+主库），先筛后搜+关联扩展检索。
    /// </summary>
    internal class MemoryService
    {
        private readonly MemoryRepository memories;
        private readonly TempMemoryRepository tempMemories;
        private readonly MemoryLinkRepository memoryLinks;
        private readonly IEmbeddingProvider embedding;

        // 综合排序权重
        private const float SimilarityWeight = 0.5f;
        private const float ImportanceWeight = 0.3f;
        private const float LinkWeight = 0.2f;
        private const float TempBoost = 0.1f; // 临时库偏置

        public MemoryService(
            MemoryRepository memories,
            TempMemoryRepository tempMemories,
            MemoryLinkRepository memoryLinks,
            IEmbeddingProvider embedding)
        {
            this.memories = memories;
            this.tempMemories = tempMemories;
            this.memoryLinks = memoryLinks;
            this.embedding = embedding;
        }

        /// <summary>
        /// 写入临时记忆库。框架自动填标签，写入时生成 embedding。
        /// </summary>
        public async Task<TempMemoryEntry> StoreAsync(
            string content,
            int? personId = null, int? channelId = null, int? topicId = null,
            int? sourceMessageId = null)
        {
            byte[]? embeddingBytes = null;
            try
            {
                var vec = await embedding.GetEmbeddingAsync(content);
                embeddingBytes = SiliconFlowEmbeddingProvider.FloatsToBytes(vec);
            }
            catch (Exception)
            {
                // embedding 生成失败不阻塞写入，后续做梦时可补
            }

            return await tempMemories.CreateAsync(
                content, embeddingBytes, personId, channelId, topicId, sourceMessageId);
        }

        /// <summary>
        /// 检索相关记忆。先查临时库，再查主库（标签过滤→向量精排→关联扩展→综合排序）。
        /// </summary>
        public async Task<List<ScoredMemory>> RecallAsync(
            int personId, int channelId, int topicId,
            string query, int topK = 10, bool includeLinks = true)
        {
            // 生成 query embedding
            float[]? queryVec = null;
            try
            {
                queryVec = await embedding.GetEmbeddingAsync(query);
            }
            catch (Exception)
            {
                // embedding 不可用时退化为无向量排序
            }

            var scored = new List<ScoredMemory>();

            // 1. 查临时库
            var tempEntries = await tempMemories.GetByTagsAsync(personId, channelId, topicId);
            foreach (var t in tempEntries)
            {
                float sim = ComputeSimilarity(queryVec, t.Embedding);
                scored.Add(new ScoredMemory
                {
                    Id = t.Id,
                    Content = t.Content,
                    Score = sim * SimilarityWeight + TempBoost,
                    IsTemp = true
                });
            }

            // 2. 查主库：标签过滤 + 向量精排
            var mainEntries = await memories.GetByTagsAsync(personId, channelId, topicId);
            var mainScored = mainEntries.Select(m => new
            {
                Entry = m,
                Similarity = ComputeSimilarity(queryVec, m.Embedding)
            })
            .OrderByDescending(x => x.Similarity * SimilarityWeight + x.Entry.Importance * ImportanceWeight)
            .Take(topK * 2)
            .ToList();

            foreach (var x in mainScored)
            {
                scored.Add(new ScoredMemory
                {
                    Id = x.Entry.Id,
                    Content = x.Entry.Content,
                    Score = x.Similarity * SimilarityWeight + x.Entry.Importance * ImportanceWeight,
                    IsTemp = false
                });
            }

            // 3. 关联扩展
            if (includeLinks && mainScored.Count > 0)
            {
                var topIds = mainScored.Take(topK).Select(x => x.Entry.Id).ToList();
                var links = await memoryLinks.GetLinksForAsync(topIds);

                var existingIds = new HashSet<int>(scored.Select(s => s.Id));
                var linkedIds = new HashSet<int>();
                var linkStrengths = new Dictionary<int, float>();

                foreach (var link in links)
                {
                    int linkedId = topIds.Contains(link.SourceId) ? link.TargetId : link.SourceId;
                    if (!existingIds.Contains(linkedId) && !linkedIds.Contains(linkedId))
                    {
                        linkedIds.Add(linkedId);
                        linkStrengths[linkedId] = link.Strength;
                    }
                }

                if (linkedIds.Count > 0)
                {
                    var linkedEntries = await memories.GetByIdsAsync(linkedIds.ToList());
                    foreach (var entry in linkedEntries)
                    {
                        float sim = ComputeSimilarity(queryVec, entry.Embedding);
                        float linkStr = linkStrengths.GetValueOrDefault(entry.Id, 0f);
                        scored.Add(new ScoredMemory
                        {
                            Id = entry.Id,
                            Content = entry.Content,
                            Score = sim * SimilarityWeight + entry.Importance * ImportanceWeight + linkStr * LinkWeight,
                            IsTemp = false
                        });
                    }
                }
            }

            // 4. 综合排序，取 topN
            var result = scored
                .OrderByDescending(s => s.Score)
                .Take(topK)
                .ToList();

            // 5. 更新主库记忆的 LastAccessedAt
            foreach (var s in result.Where(s => !s.IsTemp))
            {
                var entry = mainEntries.FirstOrDefault(m => m.Id == s.Id);
                if (entry != null)
                    await memories.UpdateAsync(entry);
            }

            return result;
        }

        /// <summary>删除指定记忆。</summary>
        public async Task ForgetAsync(int memoryId)
        {
            var memory = await memories.GetByIdAsync(memoryId);
            if (memory != null)
                await memories.DeleteAsync(memory);
        }

        /// <summary>计算余弦相似度。任一向量为 null 时返回 0。</summary>
        private static float ComputeSimilarity(float[]? a, byte[]? bBytes)
        {
            if (a == null || bBytes == null || bBytes.Length == 0) return 0f;
            var b = SiliconFlowEmbeddingProvider.BytesToFloats(bBytes);
            return CosineSimilarity(a, b);
        }

        /// <summary>余弦相似度。</summary>
        private static float CosineSimilarity(float[] a, float[] b)
        {
            if (a.Length != b.Length) return 0f;
            float dot = 0f, normA = 0f, normB = 0f;
            for (int i = 0; i < a.Length; i++)
            {
                dot += a[i] * b[i];
                normA += a[i] * a[i];
                normB += b[i] * b[i];
            }
            var denom = MathF.Sqrt(normA) * MathF.Sqrt(normB);
            return denom == 0f ? 0f : dot / denom;
        }
    }

    /// <summary>带评分的记忆检索结果。</summary>
    internal class ScoredMemory
    {
        public int Id { get; set; }
        public string Content { get; set; } = "";
        public float Score { get; set; }
        public bool IsTemp { get; set; }
    }
}