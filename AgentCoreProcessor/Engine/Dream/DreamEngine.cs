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
    internal enum SleepLevel { Daydream, Nap, DeepSleep }

    /// <summary>
    /// 做梦引擎。每次睡觉创建，完成后销毁。
    /// 两阶段：秩序（temp入库）+ 巡逻（主库维护）。
    /// </summary>
    internal class DreamEngine : ISubEngine
    {
        public string EngineType => "Dream";
        public bool IsAlive { get; private set; } = true;

        private readonly ISystemContext ctx;
        private readonly SleepLevel level;
        private readonly DreamEngineSpawnCheck spawnCheck;

        private readonly RelationClassificationCore relationClassificationCore = new();
        private readonly WeightCore weightCore = new();
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

        public DreamEngine(ISystemContext ctx, SleepLevel level, int maxFragments,
            DreamEngineSpawnCheck spawnCheck)
        {
            this.ctx = ctx;
            this.level = level;
            this.spawnCheck = spawnCheck;
        }

        // ============================================================
        // 主循环
        // ============================================================

        public async Task RunAsync()
        {
            var parentCtx = SignalContext.Current;
            var lifeCtx = Signal.Continue(
                parentCtx?.SignalId ?? Signal.NewId(), parentCtx?.CurrentSpanId,
                "dream:main", LogGroup.Engine, "Dream引擎",
                new { engineType = EngineType, level = level.ToString() });

            await CleanupExpiredMemoriesAsync();

            var session = await ctx.DreamLogs.CreateSessionAsync(new DreamSession
            {
                Level = level.ToString(),
                StartTime = DateTime.Now,
            });
            currentSessionId = session.Id;

            ctx.CurrentSleepState = level switch
            {
                SleepLevel.Daydream => SleepState.Daydream,
                SleepLevel.Nap => SleepState.Nap,
                SleepLevel.DeepSleep => SleepState.DeepSleep,
                _ => SleepState.None
            };
            var startTime = DateTime.Now;

            var cfg = spawnCheck.GetConfig();
            int patrolBudget = level switch
            {
                SleepLevel.Daydream => cfg.DaydreamPatrolSteps,
                SleepLevel.Nap => cfg.NapPatrolSteps,
                SleepLevel.DeepSleep => cfg.MaxPatrolSteps,
                _ => 3
            };

            // ---- 秩序阶段 ----
            if (level == SleepLevel.Nap || level == SleepLevel.DeepSleep)
            {
                await RunOrderPhaseAsync(cfg);
                if (shouldWake) goto finish;
            }

            // ---- 巡逻 ----
            await RunPatrolAsync(patrolBudget, cfg);

        finish:
            if (shouldWake)
                Signal.Event(LogGroup.Engine, "做梦被唤醒", new { steps = StepsCompleted });

            int processed = level == SleepLevel.DeepSleep ? StepsCompleted : 0;
            spawnCheck.OnDreamCompleted(level, processed);

            await PersistSessionAsync(startTime, StepsCompleted);

            ctx.CurrentSleepState = SleepState.None;
            IsAlive = false;

            lifeCtx.Close(new { engineType = EngineType, reason = "completed", steps = StepsCompleted, phase = CurrentPhase ?? "none" });
        }

        // ============================================================
        // 秩序阶段
        // ============================================================

        private async Task RunOrderPhaseAsync(DreamConfig cfg)
        {
            var temps = await ctx.TempMemories.GetAllAsync();
            if (temps.Count == 0)
            {
                Signal.Event(LogGroup.Engine, "秩序阶段", new { reason = "无临时记忆，跳过" });
                return;
            }

            CurrentPhase = "order";
            CurrentInputDescription = $"秩序阶段: {temps.Count} 条临时记忆";
            Signal.Event(LogGroup.Engine, "秩序阶段开始", new { tempCount = temps.Count });

            // 1. 为每条 temp 嵌出向量并搜索主库候选
            var orderEntries = new List<(TempMemoryEntry Temp, List<MemoryEntry> Candidates, List<float> CosScores)>();
            foreach (var temp in temps)
            {
                if (shouldWake) return;

                float[] emb;
                try { emb = await ctx.Embedding.GetEmbeddingAsync(temp.Content); }
                catch { continue; }

                var candidates = await ctx.Memories.FindSimilarAsync(
                    VectorUtil.FloatsToBytes(emb), cfg.EmbeddingTopK, 0.7f);
                if (candidates.Count == 0)
                {
                    orderEntries.Add((temp, new List<MemoryEntry>(), new List<float>()));
                    continue;
                }

                var cosScores = new List<float>();
                foreach (var c in candidates)
                {
                    if (c.Embedding != null)
                    {
                        var cEmb = VectorUtil.BytesToFloats(c.Embedding);
                        cosScores.Add(VectorUtil.CosineSimilarity(emb, cEmb));
                    }
                    else
                    {
                        cosScores.Add(0.7f); // 保守估计
                    }
                }
                orderEntries.Add((temp, candidates, cosScores));
            }

            // 2. 关系分类（分批 LLM，每轮一个中心 + N 个候选）
            var relationResults = new Dictionary<int, List<(int CandidateId, float Support)>>();
            foreach (var entry in orderEntries)
            {
                if (shouldWake) return;
                if (entry.Candidates.Count == 0) continue;

                var meaningful = entry.Candidates
                    .Select((c, i) => (Candidate: c, Cos: entry.CosScores[i]))
                    .Where(x => x.Cos >= cfg.OrderClassifyMinCos)
                    .ToList();
                if (meaningful.Count == 0) continue;

                // 构建临时 MemoryEntry 作为中心节点
                var center = TempToMemoryEntry(entry.Temp);

                // 分批：每次最多 RelationBatchMaxTargets 个候选
                for (int i = 0; i < meaningful.Count; i += cfg.RelationBatchMaxTargets)
                {
                    var batchTargets = meaningful.Skip(i).Take(cfg.RelationBatchMaxTargets)
                        .Select(x => x.Candidate).ToList();
                    var batchCos = meaningful.Skip(i).Take(cfg.RelationBatchMaxTargets)
                        .Select(x => x.Cos).ToList();

                    var result = await ClassifyRelationsAsync(center, batchTargets, batchCos);
                    if (result == null) continue;

                    foreach (var (targetIdx, support) in result)
                    {
                        if (targetIdx < 0 || targetIdx >= batchTargets.Count) continue;
                        var candidateId = batchTargets[targetIdx].Id;
                        if (!relationResults.ContainsKey(center.Id))
                            relationResults[center.Id] = new();
                        relationResults[center.Id].Add((candidateId, support));
                    }
                }
            }

            // 3. 入库 + 建边
            int inserted = 0, linked = 0, conflicted = 0;
            foreach (var entry in orderEntries)
            {
                if (shouldWake) return;

                var temp = entry.Temp;
                float[]? emb = null;
                try { emb = await ctx.Embedding.GetEmbeddingAsync(temp.Content); }
                catch { /* embedding 失败不阻塞入库 */ }

                var insertedMem = await ctx.Memories.CreateAsync(
                    temp.Content,
                    emb != null ? VectorUtil.FloatsToBytes(emb) : null,
                    temp.PersonId,
                    temp.ChannelId,
                    certainty: TempConfidenceToFloat(temp.Confidence),
                    type: temp.Type ?? MemoryType.Fact,
                    subject: temp.Subject);
                inserted++;

                // 应用关系分类结果
                if (relationResults.TryGetValue(temp.Id, out var relations))
                {
                    foreach (var (candidateId, support) in relations)
                    {
                        if (Math.Abs(support) < 0.1f) continue;

                        await ctx.MemoryLinks.CreateOrUpdateAsync(
                            insertedMem.Id, candidateId,
                            Math.Abs(support), // Relevance = |support|
                            "semantic",
                            Math.Clamp(support, -1f, 1f)); // Support = support
                        linked++;

                        if (support < 0)
                            conflicted++;

                        currentDetails.Add(new FragmentDetailRecord
                        {
                            Action = support < 0 ? "conflict_link" : "order_link",
                            MemoryId = insertedMem.Id,
                            Note = $"→#{candidateId}, support={support:F2}"
                        });
                    }
                }

                // Weight 评估（批量评估新记忆的 importance + certainty）
                // 这里按单条评估，因为在秩序阶段是一次性处理
            }

            // 4. 清理 temp 表
            foreach (var t in temps)
                await ctx.TempMemories.DeleteAsync(t);

            // 5. ChangePropagation — 扩散新节点带来的变更
            if (inserted > 0)
            {
                var seeds = new List<ChangePropagation.NodeChange>();
                // 每条新插入的记忆都是变更源
                // (此处 delta 由后续巡逻补正，秩序阶段先传播 insert 事件)
                await ChangePropagation.PropagateAsync(
                    seeds, cfg.ChangePropagationEpsilon,
                    ctx.Memories, ctx.MemoryLinks);
            }

            Signal.Event(LogGroup.Engine, "秩序阶段完成",
                new { inserted, linked, conflicted, tempCount = temps.Count });
        }

        // ============================================================
        // 巡逻
        // ============================================================

        private async Task RunPatrolAsync(int maxSteps, DreamConfig cfg)
        {
            CurrentPhase = "patrol";
            StepsTotal = maxSteps;

            patrol = new PatrolState { StepsBudget = maxSteps };

            Signal.Event(LogGroup.Engine, "巡逻开始", new { stepsBudget = maxSteps });

            MemoryEntry? current = await SelectColdStartNode();
            if (current == null)
            {
                Signal.Event(LogGroup.Engine, "巡逻", new { reason = "无可巡逻节点" });
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
                    // 死胡同，重新冷启动
                    next = await SelectColdStartNode();
                    if (next == null || patrol.Visited.Contains(next.Id))
                        break; // 所有节点都走完了
                }

                current = next;
            }

            // 巡逻结束：清空三角缓冲
            await FlushTriangleBuffer(cfg);

            Signal.Event(LogGroup.Engine, "巡逻完成",
                new { stepsTaken = patrol.StepsTaken, visitedCount = patrol.Visited.Count });
        }

        private async Task<MemoryEntry?> SelectColdStartNode()
        {
            var poolSize = spawnCheck.GetConfig().ColdStartPoolSize;
            var recent = await ctx.Memories.GetRecentAsync(1000);
            if (recent.Count == 0) return null;

            // 按 LastDreamTime 排序，从未被巡逻过的放前面
            var ordered = recent
                .OrderBy(m => m.LastDreamTime ?? DateTime.MinValue)
                .Take(poolSize)
                .ToList();

            if (ordered.Count == 0) return null;

            // 冷度加权随机
            var now = DateTime.Now;
            var weights = ordered.Select(m =>
            {
                var last = m.LastDreamTime ?? DateTime.MinValue;
                double days = (now - last).TotalDays;
                if (days <= 0) days = 0.1;
                return (float)Math.Min(days, 30.0); // 封顶 30 天
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

                    // embedding 验证
                    float cos = 0f;
                    if (neighbors[i].Embedding != null && neighbors[j].Embedding != null)
                    {
                        var embA = VectorUtil.BytesToFloats(neighbors[i].Embedding!);
                        var embB = VectorUtil.BytesToFloats(neighbors[j].Embedding!);
                        cos = VectorUtil.CosineSimilarity(embA, embB);
                    }

                    if (cos >= cfg.TriangleClassifyMinCos)
                    {
                        // 加入 LLM 缓冲
                        if (!patrol!.TriangleBuffer.ContainsKey(node.Id))
                            patrol.TriangleBuffer[node.Id] = new();
                        patrol.TriangleBuffer[node.Id].Add((neighbors[j].Id, cos));
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

                // 删除触发传播
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

            // 三角缓冲攒够就清
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

                // 去重：按 targetId 去重
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

                // 分批
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
        // LLM 关系分类（分批，每轮一个中心 + N 个候选）
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
        // 辅助：Temp → 临时 MemoryEntry（用于关系分类的 prompt 注入）
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

        /// <summary>临时记忆 Confidence 字符串 → Certainty 浮点桥接</summary>
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

            switch (level)
            {
                case SleepLevel.Daydream:
                    if (msg.IsMentioned) shouldWake = true;
                    break;
                case SleepLevel.Nap:
                    if (msg.IsMentioned && ContainsWakeKeyword(msg.Content))
                        shouldWake = true;
                    else if (msg.IsMentioned)
                        _ = ForceSleepTalkAsync(msg.Content);
                    break;
                case SleepLevel.DeepSleep:
                    break;
            }
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

        private async Task MaybeSleepTalkAsync(string? fragmentSummary)
        {
            if (ctx.MuteMode) return;
            if (string.IsNullOrEmpty(fragmentSummary)) return;
            var chance = level switch
            {
                SleepLevel.DeepSleep => 0.25,
                SleepLevel.Nap => 0.15,
                _ => 0.0
            };
            if (Random.Shared.NextDouble() >= chance) return;

            try
            {
                var channels = await ctx.Session.GetAllChannelsAsync();
                if (channels.Count == 0) return;
                var targetChannel = channels[Random.Shared.Next(channels.Count)];
                var parts = targetChannel.Name.Split(':', 2);
                if (parts.Length != 2) return;
                var talk = await sleepTalkCore.GenerateAsync(fragmentSummary);
                if (string.IsNullOrWhiteSpace(talk)) return;
                if (talk.Length > 50) talk = talk[..50];
                var sentId = await ctx.Adapters.SendMessageAsync(parts[0], new OutgoingMessage
                {
                    ChannelId = parts[1],
                    Content = talk
                });
                await ctx.Session.SaveBotMessageAsync(targetChannel.Id, talk, sentId);
            }
            catch (Exception ex)
            {
                Signal.Warn(LogGroup.Engine, "梦话发送失败(概率)", new { error = ex.GetType().Name, message = ex.Message });
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
