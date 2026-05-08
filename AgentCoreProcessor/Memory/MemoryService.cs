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
        private readonly PersonaMemoryRepository? personaMemories;
        private readonly IEmbeddingProvider embedding;

        // 综合排序权重
        private const float SimilarityWeight = 0.5f;
        private const float ImportanceWeight = 0.3f;
        private const float LinkWeight = 0.2f;
        private const float TempBoost = 0.1f; // 临时库偏置
        private const float TagMatchBoost = 0.1f; // 每个匹配标签的额外加权
        private const float MinRecallScore = 0.25f; // 召回最低分数门槛，低于此值不返回
        private const float PersonaPenalty = 0.05f; // 人设记忆降权，确保真记忆优先
        private const float PersonaMinScore = 0.12f; // 人设记忆独立门槛（低于主门槛，因为没有标签/重要性加成）

        public MemoryService(
            MemoryRepository memories,
            TempMemoryRepository tempMemories,
            MemoryLinkRepository memoryLinks,
            IEmbeddingProvider embedding,
            PersonaMemoryRepository? personaMemories = null)
        {
            this.memories = memories;
            this.tempMemories = tempMemories;
            this.memoryLinks = memoryLinks;
            this.embedding = embedding;
            this.personaMemories = personaMemories;
        }

        /// <summary>
        /// 写入临时记忆库。框架自动填标签，写入时生成 embedding。
        /// </summary>
        public async Task<TempMemoryEntry> StoreAsync(
            string content,
            int? personId = null, int? channelId = null,
            int? sourceMessageId = null, string confidence = "high",
            string type = MemoryType.Fact, string? subject = null)
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
                content, embeddingBytes, personId, channelId, sourceMessageId, confidence, type, subject);
        }

        /// <summary>
        /// 检索相关记忆。全量候选 + 软加分精排（person/channel 不做硬过滤）。
        /// </summary>
        public async Task<List<ScoredMemory>> RecallAsync(
            int personId, int channelId,
            string query, int topK = 10, bool includeLinks = true, bool includePersona = false)
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

            // 1. 查临时库（全量 + 软加分）
            var tempResults = await tempMemories.GetAllWithMatchScoreAsync(personId, channelId);
            foreach (var (t, matchCount) in tempResults)
            {
                float sim = VectorUtil.ComputeSimilarity(queryVec, t.Embedding);
                scored.Add(new ScoredMemory
                {
                    Id = t.Id,
                    Content = t.Content,
                    Score = sim * SimilarityWeight + TempBoost + matchCount * TagMatchBoost,
                    IsTemp = true,
                    Confidence = t.Confidence
                });
            }

            // 2. 查主库（全量 + 软加分 + 向量精排）
            var mainResults = await memories.GetAllWithMatchScoreAsync(personId, channelId);
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
                    IsTemp = false,
                    Confidence = x.Entry.Confidence
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
                            IsTemp = false,
                            Confidence = entry.Confidence
                        });
                    }
                }
            }

            // 4. 人设记忆（仅聊天时启用，降权确保真记忆优先）
            if (includePersona && personaMemories != null)
            {
                var personaAll = await personaMemories.GetAllAsync();
                foreach (var p in personaAll)
                {
                    float sim = VectorUtil.ComputeSimilarity(queryVec, p.Embedding);
                    float personaScore = sim * SimilarityWeight - PersonaPenalty;
                    if (personaScore >= PersonaMinScore)
                    {
                        scored.Add(new ScoredMemory
                        {
                            Id = -p.Id,
                            Content = p.Content,
                            Score = personaScore,
                            IsTemp = false,
                            IsPersona = true
                        });
                    }
                }
            }

            // 5. 综合排序，过滤低分（人设记忆用独立门槛），取 topN
            var result = scored
                .Where(s => s.Score >= (s.IsPersona ? PersonaMinScore : MinRecallScore))
                .OrderByDescending(s => s.Score)
                .Take(topK)
                .ToList();

            // 6. 更新主库记忆的 LastAccessedAt（跳过临时和人设记忆）
            foreach (var s in result.Where(s => !s.IsTemp && !s.IsPersona))
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

        /// <summary>
        /// 应用用户反馈到最相关的记忆。
        /// positive → 置信度升为 high；negative → 标记反馈 + 降低重要性。
        /// </summary>
        public async Task ApplyFeedbackAsync(
            int personId, string feedbackContent,
            string sentiment, string? correction)
        {
            float[]? queryVec = null;
            try { queryVec = await embedding.GetEmbeddingAsync(feedbackContent); }
            catch { return; } // embedding 不可用时无法匹配

            // 搜索主库中该用户的记忆
            var personMemories = await memories.GetByPersonAsync(personId);
            MemoryEntry? bestMatch = null;
            float bestSim = 0.6f; // 最低匹配阈值

            foreach (var m in personMemories)
            {
                float sim = VectorUtil.ComputeSimilarity(queryVec, m.Embedding);
                if (sim > bestSim)
                {
                    bestSim = sim;
                    bestMatch = m;
                }
            }

            // 也搜索临时库
            var tempAll = await tempMemories.GetAllWithMatchScoreAsync(personId, null);
            TempMemoryEntry? bestTempMatch = null;
            float bestTempSim = 0.6f;

            foreach (var (t, _) in tempAll)
            {
                float sim = VectorUtil.ComputeSimilarity(queryVec, t.Embedding);
                if (sim > bestTempSim)
                {
                    bestTempSim = sim;
                    bestTempMatch = t;
                }
            }

            // 优先匹配主库（更稳定），否则匹配临时库
            if (bestMatch != null && bestSim >= bestTempSim)
            {
                if (sentiment == "positive")
                {
                    bestMatch.Confidence = "high";
                }
                else if (sentiment == "negative")
                {
                    bestMatch.Feedback = "negative";
                    if (bestMatch.Importance > 0.3f)
                        bestMatch.Importance = 0.2f;
                }
                await memories.UpdateAsync(bestMatch);
            }
            else if (bestTempMatch != null)
            {
                if (sentiment == "positive")
                {
                    bestTempMatch.Confidence = "high";
                    await tempMemories.UpdateAsync(bestTempMatch);
                }
                else if (sentiment == "negative")
                {
                    // 临时记忆被否定，直接删除
                    await tempMemories.DeleteAsync(bestTempMatch);
                }
            }
        }
    }
    internal class ScoredMemory
    {
        public int Id { get; set; }
        public string Content { get; set; } = "";
        public float Score { get; set; }
        public bool IsTemp { get; set; }
        public string Confidence { get; set; } = "high";
        public bool IsPersona { get; set; }
    }
}