using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AgentCoreProcessor.Client;
using AgentCoreProcessor.Core;
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
        private const float SimilarityWeight = 0.45f;
        private const float ImportanceWeight = 0.25f;
        private const float CertaintyWeight = 0.15f;
        private const float LinkWeight = 0.15f;
        private const float HeatWeight = 0.10f;
        private const float TagMatchBoost = 0.1f;
        private const float KeywordBoost = 0.15f;
        private const float SubjectBoost = 0.2f;
        private const float MinRecallScore = 0.25f;
        private const float PersonaPenalty = 0.05f;
        private const float PersonaMinScore = 0.12f;
        // 目标逼近模型倍率
        private const float TempHeatBoostRate = 0.10f;
        private const float RecallImportanceRate = 0.05f;
        private const float DuplicateImportanceRate = 0.20f;
        private const float ImportanceDecayRate = 0.03f; // 每日衰减率

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

            // 1. 查临时库（全量 + 热度加权）
            var tempResults = await tempMemories.GetAllWithMatchScoreAsync(personId, channelId);
            foreach (var (t, matchCount) in tempResults)
            {
                float sim = VectorUtil.ComputeSimilarity(queryVec, t.Embedding);
                float heatScore = HeatWeight * MathF.Sqrt(t.Heat);
                scored.Add(new ScoredMemory
                {
                    Id = t.Id,
                    Content = t.Content,
                    Subject = t.Subject,
                    Score = sim * SimilarityWeight + heatScore + matchCount * TagMatchBoost,
                    IsTemp = true,
                    Certainty = t.Confidence == "high" ? 1.0f : 0.3f
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
                + x.Entry.Certainty * CertaintyWeight
                + x.MatchCount * TagMatchBoost)
            .Take(topK * 2)
            .ToList();

            foreach (var x in mainScored)
            {
                scored.Add(new ScoredMemory
                {
                    Id = x.Entry.Id,
                    Content = x.Entry.Content,
                    Subject = x.Entry.Subject,
                    Score = x.Similarity * SimilarityWeight
                        + x.Entry.Importance * ImportanceWeight
                        + x.Entry.Certainty * CertaintyWeight
                        + x.MatchCount * TagMatchBoost,
                    IsTemp = false,
                    Certainty = x.Entry.Certainty
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
                        linkStrengths[linkedId] = link.Relevance;
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
                            Certainty = entry.Certainty
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

            // 6. 命中追踪：热度奖励 + 目标逼近重要度
            var now = DateTime.Now;
            var mainEntryDict = mainEntries.ToDictionary(m => m.Id);
            var tempEntryDict = tempResults.ToDictionary(x => x.Entry.Id, x => x.Entry);

            for (int rank = 0; rank < result.Count; rank++)
            {
                var s = result[rank];
                if (s.IsTemp && tempEntryDict.TryGetValue(s.Id, out var tempEntry))
                {
                    // 临时库热度：目标逼近 + 排名加权
                    float rankBonus = TempHeatBoostRate / MathF.Sqrt(rank + 1);
                    tempEntry.Heat += (1 - tempEntry.Heat) * rankBonus;
                    await tempMemories.UpdateAsync(tempEntry);
                }
                else if (!s.IsTemp && !s.IsPersona && mainEntryDict.TryGetValue(s.Id, out var entry))
                {
                    entry.RecallCount++;

                    // 懒补衰减
                    if (entry.LastRecalledAt.HasValue)
                    {
                        var days = (float)(now - entry.LastRecalledAt.Value).TotalDays;
                        entry.Importance *= MathF.Pow(1 - ImportanceDecayRate, days);
                    }
                    entry.LastRecalledAt = now;

                    // 目标逼近奖励
                    entry.Importance += (1 - entry.Importance) * RecallImportanceRate;
                    await memories.UpdateAsync(entry);
                }
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
                    bestMatch.Certainty = Math.Clamp(bestMatch.Certainty + 0.2f, 0f, 1f);
                }
                else if (sentiment == "negative")
                {
                    bestMatch.Feedback = "negative";
                    bestMatch.Certainty = Math.Clamp(bestMatch.Certainty - 0.3f, 0f, 1f);
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
        public string? Subject { get; set; }
        public float Score { get; set; }
        public bool IsTemp { get; set; }
        public float Certainty { get; set; } = 1.0f;
        public bool IsPersona { get; set; }
    }
}