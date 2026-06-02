using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AgentCoreProcessor.Adapter;
using AgentCoreProcessor.Config;
using AgentCoreProcessor.Core;
using AgentCoreProcessor.Database;
using AgentCoreProcessor.Logging;
using AgentCoreProcessor.Util;
using Newtonsoft.Json.Linq;

namespace AgentCoreProcessor.Engine
{
    /// <summary>
    /// 做梦引擎。每次睡觉创建，完成后销毁。
    /// 两阶段：秩序（temp入库）+ 巡逻（主库维护）。
    /// </summary>
    internal class DreamEngine : ISubEngine
    {
        public string EngineType => "Dream";
        public bool IsAlive { get; private set; } = true;

        private readonly ISystemContext ctx;
        private readonly DreamEngineSpawnCheck spawnCheck;

        private readonly RelationClassificationCore relationClassificationCore = new();
        private readonly SleepTalkCore sleepTalkCore = new();

        private volatile bool shouldWake = false;

        // 巡逻状态
        private class PatrolState
        {
            public int StepsBudget;
            public int StepsTaken;
            public HashSet<int> Visited = new();
            // LLM 三角缓冲：(centerNodeId, [(targetNodeId, cosScore), ...])
            public Dictionary<int, List<(int TargetId, float Cos)>> TriangleBuffer = new();
        }

        private PatrolState? patrol;

        // WebUI 实时进度
        internal string? CurrentPhase { get; private set; }
        internal int StepsCompleted { get; private set; }
        internal int StepsTotal { get; private set; }
        internal string? CurrentInputDescription { get; private set; }
        internal FragmentRecord? LastCompletedRecord { get; private set; }
        internal IReadOnlyList<FragmentRecord> CompletedFragments => fragmentRecords;

        private readonly List<FragmentRecord> fragmentRecords = new();
        private List<FragmentDetailRecord> currentDetails = new();
        private string? currentOutputRaw;
        private int currentSessionId;

        public DreamEngine(ISystemContext ctx,
            DreamEngineSpawnCheck spawnCheck)
        {
            this.ctx = ctx;
            this.spawnCheck = spawnCheck;
        }

        // ============================================================
        // 主循环
        // ============================================================

        private static readonly SemaphoreSlim EmbedSemaphore = new(4);
        private const float TempHeatDecayRate = 0.90f; // 每分钟衰减 10%

        public async Task RunAsync()
        {
            var parentCtx = SignalContext.Current;
            var lifeCtx = Signal.Continue(
                parentCtx?.SignalId ?? Signal.NewId(), parentCtx?.CurrentSpanId,
                "dream:main", LogGroup.Engine, "Dream引擎",
                new { engineType = EngineType });

            ctx.CurrentSleepState = SleepState.DeepSleep;
            var cfg = spawnCheck.GetConfig();

            while (!shouldWake)
            {
                await CleanupExpiredMemoriesAsync();

                // ---- Heat 衰减 ----
                var allTemps = await ctx.TempMemories.GetAllAsync();
                if (allTemps.Count > 0)
                {
                    var now = DateTime.Now;
                    foreach (var t in allTemps)
                    {
                        var minutes = (float)(now - t.CreatedAt).TotalMinutes;
                        if (minutes > 0.1f)
                        {
                            t.Heat *= MathF.Pow(TempHeatDecayRate, minutes);
                            await ctx.TempMemories.UpdateAsync(t);
                        }
                    }
                }

                // ---- 秩序阶段：只处理冷 temp ----
                var coldTemps = allTemps
                    .Where(t => t.Heat < 0.15f)
                    .OrderByDescending(t => TempConfidenceToFloat(t.Confidence))
                    .ThenBy(t => t.CreatedAt)
                    .ToList();

                if (coldTemps.Count > 0)
                {
                    await RunOrderPhaseAsync(cfg, coldTemps);
                    if (shouldWake) break;
                }
                else
                {
                    Signal.Event(LogGroup.Engine, "维护循环", new { temps = allTemps.Count, coldTemps = 0 });
                }

                // ---- 巡逻 ----
                // 新会话每轮创建
                var session = await ctx.DreamLogs.CreateSessionAsync(new DreamSession
                {
                    Level = "Dream",
                    StartTime = DateTime.Now,
                });
                currentSessionId = session.Id;
                var startTime = DateTime.Now;

                await RunPatrolAsync(cfg.MaxPatrolSteps, cfg);

                await PersistSessionAsync(startTime, StepsCompleted);

                if (shouldWake) break;

                // 休息 30 秒再下一轮
                await Task.Delay(30_000);
            }

            spawnCheck.OnDreamCompleted(StepsCompleted);

            ctx.CurrentSleepState = SleepState.None;
            IsAlive = false;

            lifeCtx.Close(new { engineType = EngineType, reason = "completed", steps = StepsCompleted, phase = CurrentPhase ?? "none" });
        }

        // ============================================================
        // 秩序阶段 — 分批流水线
        // ============================================================

        private async Task RunOrderPhaseAsync(DreamConfig cfg, List<TempMemoryEntry> temps)
        {
            if (temps.Count == 0)
            {
                Signal.Event(LogGroup.Engine, "dream:order", new { reason = "无冷临时记忆，跳过" });
                return;
            }

            CurrentPhase = "order";

            using var orderSpan = Signal.Open(LogGroup.Engine, "dream:order",
                new { tempCount = temps.Count });

            // 预加载主库 embedding 到内存（跨批次累积，保证跨批次不重复）
            var allMemories = await ctx.Memories.GetRecentAsync(int.MaxValue);
            var embCache = allMemories
                .Where(m => m.Embedding != null)
                .Select(m => (Entry: m, Emb: VectorUtil.BytesToFloats(m.Embedding!)))
                .ToList();
            Signal.Event(LogGroup.Engine, "dream:order:cache",
                new { cached = embCache.Count });

            int inserted = 0, skipped = 0, merged = 0, linked = 0, conflicted = 0, cleaned = 0;
            const int BATCH = 32;

            // 本地向量搜索（替代 FindSimilarAsync，避免 SQLite 往返）
            List<(MemoryEntry Entry, float Cos)> FindCandidates(float[] targetEmb)
            {
                var results = new List<(MemoryEntry Entry, float Cos)>();
                foreach (var (entry, emb) in embCache)
                {
                    var cos = VectorUtil.CosineSimilarity(targetEmb, emb);
                    if (cos > 0.7f)
                        results.Add((entry, cos));
                }
                results.Sort((a, b) => b.Cos.CompareTo(a.Cos));
                return results;
            }

            for (int batchStart = 0; batchStart < temps.Count; batchStart += BATCH)
            {
                var batch = temps.Skip(batchStart).Take(BATCH).ToList();

                // 并行 embed（受 Semaphore 限流）
                var batchEmbMap = new Dictionary<int, float[]>();
                var tasks = batch.Select(async t =>
                {
                    await EmbedSemaphore.WaitAsync();
                    try { batchEmbMap[t.Id] = await ctx.Embedding.GetEmbeddingAsync(t.Content); }
                    catch { /* 嵌入失败，跳过 */ }
                    finally { EmbedSemaphore.Release(); }
                });
                await Task.WhenAll(tasks);

                // 批内清洗：高置信度优先，cos ≥ 0.95 → 保留内容更详细的
                for (int i = 0; i < batch.Count; i++)
                {
                    if (!batchEmbMap.ContainsKey(batch[i].Id)) continue;
                    for (int j = i + 1; j < batch.Count; j++)
                    {
                        if (!batchEmbMap.ContainsKey(batch[j].Id)) continue;
                        var cos = VectorUtil.CosineSimilarity(batchEmbMap[batch[i].Id], batchEmbMap[batch[j].Id]);
                        if (cos >= 0.95f)
                        {
                            if (batch[i].Content.Length >= batch[j].Content.Length)
                            {
                                batchEmbMap.Remove(batch[j].Id);
                                await ctx.TempMemories.DeleteAsync(batch[j]);
                            }
                            else
                            {
                                batchEmbMap.Remove(batch[i].Id);
                                await ctx.TempMemories.DeleteAsync(batch[i]);
                                break;
                            }
                            cleaned++;
                        }
                    }
                }

                // 逐条入库（embCache 已包含主库 + 前序批次所有入库 → 跨批不重复）
                foreach (var temp in batch)
                {
                    if (shouldWake) goto finish;
                    if (!batchEmbMap.TryGetValue(temp.Id, out var emb)) continue;

                    var embBytes = VectorUtil.FloatsToBytes(emb);
                    var candidates = FindCandidates(emb);

                    var meaningful = candidates
                        .Where(c => c.Cos >= cfg.OrderClassifyMinCos)
                        .Select(c => (c.Entry, c.Cos))
                        .ToList();

                    // 高度重复 → 跳过
                    var nearDup = meaningful.FirstOrDefault(x => x.Cos >= 0.95f);
                    if (nearDup.Entry != null)
                    {
                        skipped++;
                        Signal.Event(LogGroup.Engine, "dream:order:skip_dup",
                            new { tempId = temp.Id, dupOf = nearDup.Entry.Id, cos = nearDup.Cos.ToString("F3") });
                        await ctx.TempMemories.DeleteAsync(temp);
                        continue;
                    }

                    // 关系分类
                    var relations = new List<(int TargetId, float Support)>();
                    if (meaningful.Count > 0)
                    {
                        var center = TempToMemoryEntry(temp);
                        for (int i = 0; i < meaningful.Count; i += cfg.RelationBatchMaxTargets)
                        {
                            var sub = meaningful.Skip(i).Take(cfg.RelationBatchMaxTargets).ToList();
                            var result = await ClassifyRelationsAsync(
                                center,
                                sub.Select(x => x.Entry).ToList(),
                                sub.Select(x => x.Cos).ToList());
                            if (result == null) continue;

                            foreach (var (targetIdx, support) in result)
                            {
                                if (targetIdx < 0 || targetIdx >= sub.Count) continue;
                                if (Math.Abs(support) < 0.1f) continue;
                                relations.Add((sub[targetIdx].Entry.Id, support));
                            }
                        }
                    }

                    // 合并检查（support ≥ merge 阈值）
                    var merges = relations.Where(r => r.Support >= cfg.OrderMergeMinSupport).ToList();
                    bool tempIsMerged = false;
                    foreach (var (targetId, support) in merges)
                    {
                        var candidate = meaningful.First(m => m.Entry.Id == targetId).Entry;
                        if (temp.Content.Length > candidate.Content.Length)
                        {
                            var mergedMem = await ctx.Memories.CreateAsync(
                                temp.Content, embBytes,
                                temp.PersonId, temp.ChannelId,
                                certainty: Math.Max(TempConfidenceToFloat(temp.Confidence), candidate.Certainty),
                                type: temp.Type ?? MemoryType.Fact,
                                subject: temp.Subject);
                            inserted++;
                            await RedirectLinksAsync(candidate.Id, mergedMem.Id);
                            await ctx.Memories.DeleteAsync(candidate);
                            embCache.RemoveAll(e => e.Entry.Id == candidate.Id);
                            embCache.Add((mergedMem, emb));
                            merged++;
                            Signal.Event(LogGroup.Engine, "dream:order:merge",
                                new { tempId = temp.Id, replacedId = candidate.Id, survivorId = mergedMem.Id, support = support.ToString("F2") });
                        }
                        else
                        {
                            skipped++;
                            // temp 与主库重合：重要度大步逼近
                            candidate.Importance += (1 - candidate.Importance) * 0.20f;
                            await ctx.Memories.UpdateAsync(candidate);
                            Signal.Event(LogGroup.Engine, "dream:order:merge_skip",
                                new { tempId = temp.Id, existingId = candidate.Id, support = support.ToString("F2") });
                        }
                        relations.RemoveAll(r => r.TargetId == targetId);
                        tempIsMerged = true;
                        break;
                    }
                    if (tempIsMerged)
                    {
                        await ctx.TempMemories.DeleteAsync(temp);
                        continue;
                    }

                    // 入库
                    var insertedMem = await ctx.Memories.CreateAsync(
                        temp.Content, embBytes,
                        temp.PersonId, temp.ChannelId,
                        certainty: TempConfidenceToFloat(temp.Confidence),
                        type: temp.Type ?? MemoryType.Fact,
                        subject: temp.Subject);
                    inserted++;
                    embCache.Add((insertedMem, emb));

                    Signal.Event(LogGroup.Engine, "dream:order:insert",
                        new { memId = insertedMem.Id, candidates = meaningful.Count, links = relations.Count,
                            content = temp.Content[..Math.Min(40, temp.Content.Length)] });

                    // 建边
                    foreach (var (targetId, support) in relations)
                    {
                        await ctx.MemoryLinks.CreateOrUpdateAsync(
                            insertedMem.Id, targetId,
                            Math.Abs(support), "semantic",
                            Math.Clamp(support, -1f, 1f));
                        linked++;
                        if (support < 0) conflicted++;

                        Signal.Event(LogGroup.Engine, support < 0 ? "dream:order:conflict" : "dream:order:link",
                            new { source = insertedMem.Id, target = targetId, support = support.ToString("F2") });

                        currentDetails.Add(new FragmentDetailRecord
                        {
                            Action = support < 0 ? "conflict_link" : "order_link",
                            MemoryId = insertedMem.Id,
                            Note = $"→#{targetId}, support={support:F2}"
                        });
                    }

                    await ctx.TempMemories.DeleteAsync(temp);
                }
            }

        finish:
            // ChangePropagation
            if (inserted > 0)
            {
                var seeds = new List<ChangePropagation.NodeChange>();
                await ChangePropagation.PropagateAsync(
                    seeds, cfg.ChangePropagationEpsilon,
                    ctx.Memories, ctx.MemoryLinks);
            }

            orderSpan.SetCloseDetail(new { inserted, skipped, merged, linked, conflicted, cleaned });
        }

        // ============================================================
        // 巡逻
        // ============================================================

        private async Task RunPatrolAsync(int maxSteps, DreamConfig cfg)
        {
            CurrentPhase = "patrol";
            StepsTotal = maxSteps;

            patrol = new PatrolState { StepsBudget = maxSteps };

            using var patrolSpan = Signal.Open(LogGroup.Engine, "dream:patrol",
                new { budget = maxSteps });

            MemoryEntry? current = await SelectColdStartNode();
            if (current == null)
            {
                Signal.Event(LogGroup.Engine, "dream:patrol", new { reason = "无可巡逻节点" });
                patrolSpan.SetCloseDetail(new { steps = 0, reason = "no_nodes" });
                return;
            }

            while (patrol.StepsTaken < patrol.StepsBudget)
            {
                if (shouldWake) break;

                await WalkOneStep(current, cfg);
                patrol.StepsTaken++;
                StepsCompleted = patrol.StepsTaken;
                patrol.Visited.Add(current.Id);

                // 步进：选最冷邻居
                var edges = await ctx.MemoryLinks.GetByMemoryIdAsync(current.Id);
                MemoryEntry? next = null;
                DateTime? oldestTime = null;

                foreach (var edge in edges)
                {
                    int neighborId = edge.SourceId == current.Id ? edge.TargetId : edge.SourceId;
                    if (patrol.Visited.Contains(neighborId)) continue;

                    var neighbor = await ctx.Memories.GetByIdAsync(neighborId);
                    if (neighbor == null) continue;

                    var lastTouched = neighbor.LastDreamTime ?? DateTime.MinValue;
                    if (oldestTime == null || lastTouched < oldestTime)
                    {
                        oldestTime = lastTouched;
                        next = neighbor;
                    }
                }

                if (next == null)
                {
                    next = await SelectColdStartNode();
                    if (next == null)
                    {
                        var allRecent = await ctx.Memories.GetRecentAsync(1000);
                        next = allRecent
                            .OrderBy(m => m.LastDreamTime ?? DateTime.MinValue)
                            .FirstOrDefault();
                    }
                    Signal.Event(LogGroup.Engine, "dream:patrol:cold_restart",
                        new { nextId = next?.Id, steps = patrol.StepsTaken });
                    if (next == null) break;
                }

                current = next;
            }

            await FlushTriangleBuffer(cfg);

            patrolSpan.SetCloseDetail(new
            {
                stepsTaken = patrol.StepsTaken,
                visitedCount = patrol.Visited.Count,
                bufferRemaining = patrol.TriangleBuffer.Sum(kv => kv.Value.Count)
            });
        }

        private async Task<MemoryEntry?> SelectColdStartNode()
        {
            var poolSize = spawnCheck.GetConfig().ColdStartPoolSize;
            var recent = await ctx.Memories.GetRecentAsync(1000);
            if (recent.Count == 0) return null;

            var ordered = recent
                .OrderBy(m => m.LastDreamTime ?? DateTime.MinValue)
                .Take(poolSize)
                .ToList();

            if (ordered.Count == 0) return null;

            var now = DateTime.Now;
            var weights = ordered.Select(m =>
            {
                var last = m.LastDreamTime ?? DateTime.MinValue;
                double days = (now - last).TotalDays;
                if (days <= 0) days = 0.1;
                return (float)Math.Min(days, 30.0);
            }).ToList();

            float totalWeight = weights.Sum();
            float roll = (float)(Random.Shared.NextDouble() * totalWeight);
            for (int i = 0; i < ordered.Count; i++)
            {
                roll -= weights[i];
                if (roll <= 0) return ordered[i];
            }

            return ordered[^1];
        }

        private async Task WalkOneStep(MemoryEntry node, DreamConfig cfg)
        {
            CurrentInputDescription = $"巡逻 #{node.Id}: {(node.Content.Length > 30 ? node.Content[..30] + "…" : node.Content)}";
            currentDetails = new();

            // 1. 三角闭合
            var edges = await ctx.MemoryLinks.GetByMemoryIdAsync(node.Id);
            var neighbors = new List<MemoryEntry>();
            foreach (var edge in edges)
            {
                int neighborId = edge.SourceId == node.Id ? edge.TargetId : edge.SourceId;
                var neighbor = await ctx.Memories.GetByIdAsync(neighborId);
                if (neighbor != null) neighbors.Add(neighbor);
            }

            for (int i = 0; i < neighbors.Count; i++)
            {
                for (int j = i + 1; j < neighbors.Count; j++)
                {
                    var existingLinks = await ctx.MemoryLinks.GetByMemoryIdAsync(
                    neighbors[i].Id);
                bool alreadyLinked = existingLinks.Any(l =>
                    (l.SourceId == neighbors[i].Id && l.TargetId == neighbors[j].Id) ||
                    (l.SourceId == neighbors[j].Id && l.TargetId == neighbors[i].Id));
                if (alreadyLinked) continue;

                    float cos = 0f;
                    if (neighbors[i].Embedding != null && neighbors[j].Embedding != null)
                    {
                        var embA = VectorUtil.BytesToFloats(neighbors[i].Embedding!);
                        var embB = VectorUtil.BytesToFloats(neighbors[j].Embedding!);
                        cos = VectorUtil.CosineSimilarity(embA, embB);
                    }

                    if (cos >= cfg.TriangleClassifyMinCos)
                    {
                        if (!patrol!.TriangleBuffer.ContainsKey(neighbors[i].Id))
                            patrol.TriangleBuffer[neighbors[i].Id] = new();
                        patrol.TriangleBuffer[neighbors[i].Id].Add((neighbors[j].Id, cos));
                    }
                }
            }

            // 2. 衰减计算
            var decayedImportance = MemoryDecay.ComputeDecayedImportance(
                node.Importance,
                node.LastDreamTime ?? node.CreatedAt,
                DateTime.Now,
                node.Type ?? MemoryType.Fact);

            var oldImportance = node.Importance;
            node.Importance = decayedImportance;
            node.LastDreamTime = DateTime.Now;

            if (Math.Abs(oldImportance - decayedImportance) > 0.01f)
            {
                await ctx.Memories.UpdateAsync(node);
                currentDetails.Add(new FragmentDetailRecord
                {
                    Action = "decay",
                    MemoryId = node.Id,
                    OldValue = oldImportance.ToString("F3"),
                    NewValue = decayedImportance.ToString("F3"),
                    Note = node.Content.Length > 50 ? node.Content[..50] : node.Content
                });
            }

            // 3. 过期清理
            if (decayedImportance <= cfg.DecayThreshold)
            {
                await ctx.MemoryLinks.DeleteOrphanedForMemoryAsync(node.Id);
                await ctx.Memories.DeleteAsync(node);
                currentDetails.Add(new FragmentDetailRecord
                {
                    Action = "expire_delete",
                    MemoryId = node.Id,
                    OldValue = decayedImportance.ToString("F3"),
                    Note = node.Content.Length > 50 ? node.Content[..50] : node.Content
                });

                var seeds = new List<ChangePropagation.NodeChange>
                {
                    new() { NodeId = node.Id, DeltaImportance = -node.Importance, DeltaCertainty = -node.Certainty }
                };
                await ChangePropagation.PropagateAsync(
                    seeds, cfg.ChangePropagationEpsilon,
                    ctx.Memories, ctx.MemoryLinks);
            }

            // 记录片段 + 持久化
            var rec = new FragmentRecord
            {
                Type = "patrol_step",
                StartTime = DateTime.Now,
                DurationSeconds = 0,
                Success = true,
                Summary = $"#{node.Id} imp={oldImportance:F3}→{decayedImportance:F3}",
                InputMemoryIds = node.Id.ToString(),
                Details = currentDetails
            };
            fragmentRecords.Add(rec);
            LastCompletedRecord = rec;
            await PersistFragmentAsync(rec, patrol!.StepsTaken);

            Signal.Event(LogGroup.Engine, "dream:patrol:step",
                new { step = patrol!.StepsTaken + 1, nodeId = node.Id, oldImp = oldImportance.ToString("F3"), newImp = decayedImportance.ToString("F3"), details = currentDetails.Select(d => d.Action).Distinct().ToList() });

            int buffered = patrol!.TriangleBuffer.Sum(kv => kv.Value.Count);
            if (buffered >= cfg.TriangleBufferSize)
                await FlushTriangleBuffer(cfg);
        }

        private async Task FlushTriangleBuffer(DreamConfig cfg)
        {
            if (patrol == null || patrol.TriangleBuffer.Count == 0) return;

            Signal.Event(LogGroup.Engine, "清空三角缓冲",
                new { centers = patrol.TriangleBuffer.Count, totalPairs = patrol.TriangleBuffer.Sum(kv => kv.Value.Count) });

            foreach (var (centerId, pairs) in patrol.TriangleBuffer)
            {
                if (shouldWake) break;

                var center = await ctx.Memories.GetByIdAsync(centerId);
                if (center == null) continue;

                var unique = pairs
                    .GroupBy(p => p.TargetId)
                    .Select(g => g.First())
                    .ToList();

                var targets = new List<MemoryEntry>();
                var cosScores = new List<float>();
                foreach (var (targetId, cos) in unique)
                {
                    var target = await ctx.Memories.GetByIdAsync(targetId);
                    if (target != null)
                    {
                        targets.Add(target);
                        cosScores.Add(cos);
                    }
                }

                for (int i = 0; i < targets.Count; i += cfg.RelationBatchMaxTargets)
                {
                    var batchTargets = targets.Skip(i).Take(cfg.RelationBatchMaxTargets).ToList();
                    var batchCos = cosScores.Skip(i).Take(cfg.RelationBatchMaxTargets).ToList();

                    var result = await ClassifyRelationsAsync(center, batchTargets, batchCos);
                    if (result == null) continue;

                    foreach (var (targetIdx, support) in result)
                    {
                        if (targetIdx < 0 || targetIdx >= batchTargets.Count) continue;
                        var target = batchTargets[targetIdx];
                        if (Math.Abs(support) < 0.1f) continue;

                        await ctx.MemoryLinks.CreateOrUpdateAsync(
                            center.Id, target.Id,
                            Math.Abs(support), "semantic",
                            Math.Clamp(support, -1f, 1f));
                    }
                }
            }

            patrol.TriangleBuffer.Clear();
        }

        // ============================================================
        // LLM 关系分类
        // ============================================================

        private async Task<List<(int TargetIndex, float Support)>?> ClassifyRelationsAsync(
            MemoryEntry center, List<MemoryEntry> targets, List<float> cosScores)
        {
            try
            {
                var raw = await relationClassificationCore.ClassifyAsync(center, targets, cosScores);
                currentOutputRaw = raw;

                var results = new List<(int, float)>();
                var array = JArray.Parse(TextUtil.StripMarkdownCodeFence(raw));
                foreach (var item in array)
                {
                    var idx = item["targetIndex"]?.Value<int>() ?? -1;
                    var sup = item["support"]?.Value<float>() ?? 0f;
                    if (idx >= 0 && idx < targets.Count)
                        results.Add((idx, sup));
                }
                return results;
            }
            catch (Exception ex)
            {
                Signal.Warn(LogGroup.Engine, "关系分类解析失败",
                    new { centerId = center.Id, error = ex.Message });
                return null;
            }
        }

        // ============================================================
        // 辅助
        // ============================================================

        private static MemoryEntry TempToMemoryEntry(TempMemoryEntry t)
        {
            return new MemoryEntry
            {
                Id = t.Id,
                Content = t.Content,
                Type = t.Type ?? MemoryType.Fact,
                Subject = t.Subject,
                Certainty = TempConfidenceToFloat(t.Confidence),
                PersonId = t.PersonId,
                ChannelId = t.ChannelId,
                CreatedAt = DateTime.Now
            };
        }

        private static float TempConfidenceToFloat(string? confidence) => confidence switch
        {
            "high" => 1.0f,
            "low" => 0.3f,
            _ => 0.7f
        };

        // ============================================================
        // 事件 + 打断
        // ============================================================

        public void OnEvent(EngineEvent e)
        {
            if (e is not MessageEvent msgEvent) return;
            var msg = msgEvent.Message;

            if (msg.IsMentioned && ContainsWakeKeyword(msg.Content))
                shouldWake = true;
            else if (msg.IsMentioned)
                _ = ForceSleepTalkAsync(msg.Content);
        }

        internal void ForceWake(string reason) => shouldWake = true;
        public void RequestStop() => shouldWake = true;

        private static readonly string[] WakeKeywords =
            ["起床", "醒醒", "wake", "起来", "叫醒", "别睡了", "醒来"];

        private static bool ContainsWakeKeyword(string content)
        {
            var lower = content.ToLowerInvariant();
            return WakeKeywords.Any(k => lower.Contains(k));
        }

        // ============================================================
        // 梦话
        // ============================================================

        private async Task ForceSleepTalkAsync(string triggerContent)
        {
            try
            {
                var talk = await sleepTalkCore.GenerateAsync(
                    CurrentPhase ?? "模糊的梦境",
                    triggerContent.Length > 50 ? triggerContent[..50] : triggerContent);
                if (string.IsNullOrWhiteSpace(talk)) return;
                if (talk.Length > 50) talk = talk[..50];

                var channels = await ctx.Session.GetAllChannelsAsync();
                if (channels.Count == 0) return;
                var targetChannel = channels[Random.Shared.Next(channels.Count)];
                var parts = targetChannel.Name.Split(':', 2);
                if (parts.Length != 2) return;
                var sentId = await ctx.Adapters.SendMessageAsync(parts[0], new OutgoingMessage
                {
                    ChannelId = parts[1],
                    Content = talk
                });
                await ctx.Session.SaveBotMessageAsync(targetChannel.Id, talk, sentId);
            }
            catch (Exception ex)
            {
                Signal.Warn(LogGroup.Engine, "梦话发送失败", new { error = ex.GetType().Name, message = ex.Message });
            }
        }

        // ============================================================
        // 持久化
        // ============================================================

        private async Task PersistFragmentAsync(FragmentRecord rec, int seqIndex)
        {
            try
            {
                var fragment = await ctx.DreamLogs.CreateFragmentAsync(new DreamFragment
                {
                    SessionId = currentSessionId, Type = rec.Type, SeqIndex = seqIndex,
                    StartTime = rec.StartTime, DurationSeconds = rec.DurationSeconds,
                    Success = rec.Success, Summary = rec.Summary ?? "",
                    InputMemoryIds = rec.InputMemoryIds, OutputRaw = rec.OutputRaw
                });
                if (rec.Details.Count > 0)
                {
                    var details = rec.Details.Select(d => new DreamFragmentDetail
                    {
                        FragmentId = fragment.Id, Action = d.Action,
                        MemoryId = d.MemoryId, OldValue = d.OldValue,
                        NewValue = d.NewValue, Note = d.Note
                    }).ToList();
                    await ctx.DreamLogs.CreateDetailsAsync(details);
                }
            }
            catch (Exception ex)
            {
                Signal.Warn(LogGroup.Engine, "片段即时持久化失败", new { seqIndex, type = rec.Type, error = ex.Message });
            }
        }

        private async Task PersistSessionAsync(DateTime startTime, int executed)
        {
            try
            {
                var session = await ctx.DreamLogs.GetSessionByIdAsync(currentSessionId);
                if (session != null)
                {
                    session.EndTime = DateTime.Now;
                    session.FragmentsExecuted = executed;
                    session.WasInterrupted = shouldWake;
                    await ctx.DreamLogs.UpdateSessionAsync(session);
                }
            }
            catch (Exception ex)
            {
                Signal.Warn(LogGroup.Engine, "会话结束更新失败", new { error = ex.GetType().Name, message = ex.Message });
            }
        }

        // ============================================================
        // 其他辅助
        // ============================================================

        private async Task RedirectLinksAsync(int oldId, int survivorId)
        {
            try
            {
                var links = await ctx.MemoryLinks.GetByMemoryIdAsync(oldId);
                foreach (var link in links)
                {
                    var newSource = link.SourceId == oldId ? survivorId : link.SourceId;
                    var newTarget = link.TargetId == oldId ? survivorId : link.TargetId;
                    if (newSource == newTarget) continue;
                    await ctx.MemoryLinks.CreateOrUpdateAsync(newSource, newTarget, link.Relevance, link.LinkType);
                    await ctx.MemoryLinks.DeleteAsync(link);
                }
            }
            catch (Exception ex) { Signal.Warn(LogGroup.Engine, "关联重定向失败", new { oldId, survivorId, error = ex.Message }); }
        }

        private async Task CleanupExpiredMemoriesAsync()
        {
            try
            {
                await ctx.Memories.DeleteExpiredAsync();
                await ctx.MemoryLinks.DeleteOrphanedAsync();
            }
            catch (Exception ex)
            {
                Signal.Warn(LogGroup.Engine, "过期清理失败", new { error = ex.GetType().Name, message = ex.Message });
            }
        }

        private static double ElapsedMinutes(DateTime startTime)
            => (DateTime.Now - startTime).TotalMinutes;

        private static string ComputeHash(string input)
        {
            var bytes = System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(input));
            return Convert.ToHexString(bytes)[..16];
        }
    }
}
