using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AgentCoreProcessor.Client;
using AgentCoreProcessor.Database;
using AgentCoreProcessor.Util;

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
        private const float TagMatchBoost = 0.1f; // 每个匹配标签的额外加权

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

            // 1. 查临时库（OR 模式，匹配标签数加权）
            var tempResults = await tempMemories.GetByTagsAsync(personId, channelId, topicId);
            foreach (var (t, matchCount) in tempResults)
            {
                float sim = VectorUtil.ComputeSimilarity(queryVec, t.Embedding);
                scored.Add(new ScoredMemory
                {
                    Id = t.Id,
                    Content = t.Content,
                    Score = sim * SimilarityWeight + TempBoost + matchCount * TagMatchBoost,
                    IsTemp = true
                });
            }

            // 2. 查主库（OR 模式，匹配标签数加权 + 向量精排）
            var mainResults = await memories.GetByTagsAsync(personId, channelId, topicId);
            var mainScored = mainResults.Select(x => new
            {
                x.Entry,
                x.MatchCount,
                Similarity = VectorUtil.ComputeSimilarity(queryVec, x.Entry.Embedding)
            })
            .OrderByDescending(x => x.Similarity * SimilarityWeight
                + x.Entry.Importance * ImportanceWeight
                + x.MatchCount * TagMatchBoost)
            .Take(topK * 2)
            .ToList();

            foreach (var x in mainScored)
            {
                scored.Add(new ScoredMemory
                {
                    Id = x.Entry.Id,
                    Content = x.Entry.Content,
                    Score = x.Similarity * SimilarityWeight
                        + x.Entry.Importance * ImportanceWeight
                        + x.MatchCount * TagMatchBoost,
                    IsTemp = false
                });
            }

            // mainEntries 兼容引用（关联扩展和 LastAccessedAt 更新用）
            var mainEntries = mainResults.Select(x => x.Entry).ToList();

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
                        float sim = VectorUtil.ComputeSimilarity(queryVec, entry.Embedding);
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
    }
    internal class ScoredMemory
    {
        public int Id { get; set; }
        public string Content { get; set; } = "";
        public float Score { get; set; }
        public bool IsTemp { get; set; }
    }
}