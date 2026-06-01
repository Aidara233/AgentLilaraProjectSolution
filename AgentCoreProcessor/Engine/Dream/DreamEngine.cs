using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
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
    internal enum FragmentType { Consolidation, Weight, Link, Combine, Dedup }

    /// <summary>
    /// 做梦引擎实例。每次睡觉创建，完成后销毁。
    /// 使用调度器管理资源/预算/记忆冲突，支持并行片段执行。
    /// </summary>
    internal class DreamEngine : ISubEngine
    {
        public string EngineType => "Dream";
        public bool IsAlive { get; private set; } = true;

        private readonly ISystemContext ctx;
        private readonly SleepLevel level;
        private readonly DreamEngineSpawnCheck spawnCheck;

        private readonly ConsolidationCore consolidationCore = new();
        private readonly ConsolidationFinalCore consolidationFinalCore = new();
        private readonly WeightCore weightCore = new();
        private readonly LinkCore linkCore = new();
        private readonly CombineCore combineCore = new();
        private readonly DedupCore dedupCore = new();
        private readonly SleepTalkCore sleepTalkCore = new();

        private volatile bool shouldWake = false;
        // 使用 Random.Shared（.NET 6+ 线程安全）替代 static Random

        // 实时进度（供 WebUI 读取）
        internal string? CurrentFragment { get; private set; }
        internal int FragmentsCompleted { get; private set; }
        internal int FragmentsTotal { get; private set; }
        internal DateTime? CurrentFragmentStartTime { get; private set; }
        internal string? CurrentInputDescription { get; private set; }
        internal FragmentRecord? LastCompletedRecord { get; private set; }
        internal IReadOnlyList<FragmentRecord> CompletedFragments => fragmentRecords;

        // 资源与预算（供 WebUI 读取）
        internal int AvailableResources => _scheduler?.AvailableResources ?? 0;
        internal int TotalResources => spawnCheck.GetConfig().TotalResources;
        internal int TokensUsed => _scheduler?.TokensUsed ?? 0;
        internal int MainBudget => spawnCheck.GetConfig().MainTokenBudget;
        internal int ReserveBudget => spawnCheck.GetConfig().ReserveTokenBudget;
        internal int TodoCount => _scheduler?.TodoCount ?? 0;
        internal int RunningCount => _scheduler?.RunningCount ?? 0;
        internal bool BudgetExhausted => _scheduler != null && !_scheduler.CanFill;

        internal List<RunningFragmentInfo> GetRunningFragments()
        {
            if (_scheduler == null) return new();
            var now = DateTime.Now;
            // 注意：单个 CurrentFragmentStartTime 已不够用，改用 Running 列表中的记录时间
            return _scheduler.Running.Select(d => new RunningFragmentInfo
            {
                Type = d.Type.ToString(),
                ResourceCost = d.ResourceCost,
            }).ToList();
        }

        internal class RunningFragmentInfo
        {
            public string Type { get; init; } = "";
            public int ResourceCost { get; init; }
        }

        private readonly List<FragmentRecord> fragmentRecords = new();
        private List<FragmentDetailRecord> currentDetails = new();
        private string? currentInputIds;
        private string? currentOutputRaw;
        private int currentSessionId;

        // 调度器持有引用，供 OnEvent 后检查
        private DreamScheduler? _scheduler;

        public DreamEngine(ISystemContext ctx, SleepLevel level, int maxFragments,
            DreamEngineSpawnCheck spawnCheck)
        {
            this.ctx = ctx;
            this.level = level;
            this.spawnCheck = spawnCheck;
        }

        public async Task RunAsync()
        {
            var parentCtx = AgentCoreProcessor.Logging.SignalContext.Current;
            var lifeCtx = Logging.Signal.Continue(
                parentCtx?.SignalId ?? Logging.Signal.NewId(), parentCtx?.CurrentSpanId,
                "dream:main", Logging.LogGroup.Engine, "Dream引擎",
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
            _scheduler = new DreamScheduler(cfg, PrepareFragmentAsync);

            // 初始填充
            int initialFill = level switch
            {
                SleepLevel.Daydream => 1,
                SleepLevel.Nap => 4,
                SleepLevel.DeepSleep => 8,
                _ => 1
            };
            FragmentsTotal = level switch
            {
                SleepLevel.Daydream => 1,
                SleepLevel.Nap => cfg.MaxFragmentsPerNap,
                SleepLevel.DeepSleep => cfg.MaxFragmentsPerDeepSleep,
                _ => 1
            };
            int initialAdded = await _scheduler.FillTodo(initialFill);
            Signal.Event(LogGroup.Engine, "初始填充完成",
                new { added = initialAdded, targetCount = initialFill, todoCount = _scheduler.TodoCount });

            // 检查临时记忆，空则排除 Consolidation（避免无效准备）
            var initialTemps = await ctx.TempMemories.GetAllAsync();
            if (initialTemps.Count == 0)
            {
                _scheduler.ExcludeType(FragmentType.Consolidation);
                Signal.Event(LogGroup.Engine, "无临时记忆，排除Consolidation");
            }

            // DB 状态快照（诊断用）
            var allMemories = await ctx.Memories.GetRecentAsync(1000);
            var undreamed = allMemories.Where(m => m.LastDreamTime == null).ToList();
            var totalMemoryCount = allMemories.Count;
            Signal.Event(LogGroup.Engine, "做梦DB状态",
                new
                {
                    tempCount = initialTemps.Count,
                    totalMemories = totalMemoryCount,
                    undreamedCount = undreamed.Count,
                    dreamedCount = totalMemoryCount - undreamed.Count,
                    oldestDreamed = allMemories.Where(m => m.LastDreamTime != null).OrderBy(m => m.LastDreamTime).Take(1).Select(m => m.LastDreamTime?.ToString("O")).FirstOrDefault() ?? "无"
                });

            if (!_scheduler.HasWork)
            {
                Signal.Event(LogGroup.Engine, "无待处理记忆，做梦结束（无可用的片段）",
                    new { level = level.ToString() });
            }

            int executed = 0;
            bool trustEvalDone = false;
            bool reviewStarted = false;

            // 主调度循环
            while (_scheduler.HasWork)
            {
                if (shouldWake) { Signal.Event(LogGroup.Engine, "做梦被唤醒", new { executed }); break; }
                if (level == SleepLevel.DeepSleep && ElapsedMinutes(startTime) > cfg.DeepSleepMaxMinutes)
                {
                    Signal.Event(LogGroup.Engine, "大睡超时", new { elapsedMinutes = ElapsedMinutes(startTime), maxMinutes = cfg.DeepSleepMaxMinutes, executed });
                    break;
                }

                // 派发：从 todo 取最大能塞进资源池的片段
                var dispatched = _scheduler.TryDispatch(desc => ExecuteFragmentAsync(desc));
                if (dispatched.Count > 0)
                    Signal.Event(LogGroup.Engine, "派发片段",
                        new { count = dispatched.Count, types = dispatched.Select(d => d.Type.ToString()).ToList(), runningCount = _scheduler.RunningCount, availableRes = _scheduler.AvailableResources });

                if (_scheduler.RunningCount == 0)
                {
                    if (!_scheduler.CanFill)
                    {
                        Signal.Event(LogGroup.Engine, "预算耗尽，停止调度", new { tokensUsed = _scheduler.TokensUsed, executed });
                        break;
                    }
                    int refilled = await _scheduler.FillTodo(1);
                    if (_scheduler.RunningCount == 0 && _scheduler.TodoCount == 0)
                    {
                        Signal.Event(LogGroup.Engine, "无可处理的记忆，做梦结束", new { executed });
                        break;
                    }
                    if (refilled > 0 && _scheduler.TodoCount > 0)
                    {
                        // 重新派发刚填充的片段
                        dispatched = _scheduler.TryDispatch(desc => ExecuteFragmentAsync(desc));
                    }
                    if (_scheduler.RunningCount == 0)
                        continue; // 填充了但派发失败（资源不足等），等下一轮
                }

                // 等待任意一个片段完成
                var running = _scheduler.Running;
                var completedTask = await Task.WhenAny(running.Select(d => d.RunningTask!));
                var completed = running.First(d => d.RunningTask == completedTask);

                var result = await completedTask;
                _scheduler.OnFragmentComplete(completed);

                executed++;
                FragmentsCompleted = executed;

                // 记录片段结果
                var duration = (DateTime.Now - (CurrentFragmentStartTime ?? DateTime.Now)).TotalSeconds;
                var rec = new FragmentRecord
                {
                    Type = completed.Type.ToString(),
                    StartTime = CurrentFragmentStartTime ?? DateTime.Now,
                    DurationSeconds = duration,
                    Success = result.Success,
                    Summary = result.Summary,
                    InputMemoryIds = currentInputIds,
                    OutputRaw = currentOutputRaw,
                    Details = currentDetails
                };
                fragmentRecords.Add(rec);
                LastCompletedRecord = rec;

                await MaybeSleepTalkAsync(result.Summary);
                await PersistFragmentAsync(rec, fragmentRecords.Count - 1);

                // 大睡 Phase1→Phase2 切换：临时记忆清空后启动信任评估+Review
                if (level == SleepLevel.DeepSleep && !trustEvalDone)
                {
                    var tempCount = (await ctx.TempMemories.GetAllAsync()).Count;
                    if (tempCount == 0)
                    {
                        _scheduler.ExcludeType(FragmentType.Consolidation);
                        Signal.Event(LogGroup.Engine, "临时记忆清空，排除Consolidation，进入Phase2");
                        trustEvalDone = true;
                        await ExecuteTrustEvaluationAsync();
                        if (!reviewStarted)
                        {
                            reviewStarted = true;
                            try
                            {
                                ctx.StartEngine(new ReviewEngine(ctx));
                                Signal.Event(LogGroup.Engine, "ReviewEngine启动", new { seedType = "auto" });
                            }
                            catch (Exception ex)
                            {
                                Signal.Warn(LogGroup.Engine, "ReviewEngine启动失败", new { error = ex.GetType().Name, message = ex.Message });
                            }
                        }
                    }
                }

                // 继续填充
                if (_scheduler.CanFill)
                    await _scheduler.FillTodo(1);
            }

            // 被唤醒时等待正在跑的片段完成（不丢弃进行中的工作）
            if (shouldWake && _scheduler.RunningCount > 0)
            {
                Signal.Event(LogGroup.Engine, "等待运行中片段完成", new { runningCount = _scheduler.RunningCount });
                var remaining = _scheduler.Running.Select(d => d.RunningTask!).ToArray();
                await Task.WhenAll(remaining);
                foreach (var desc in _scheduler.Running.ToList())
                {
                    _scheduler.OnFragmentComplete(desc);
                    executed++;
                    FragmentsCompleted = executed;
                }
            }

            int processed = level == SleepLevel.DeepSleep ? executed : 0;
            spawnCheck.OnDreamCompleted(level, processed);

            await PersistSessionAsync(startTime, executed);

            ctx.CurrentSleepState = SleepState.None;
            IsAlive = false;
            _scheduler = null;

            lifeCtx.Close(new { engineType = EngineType, reason = "completed", fragments = executed });
        }

        // ---- 事件处理（保持不变） ----

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

        private async Task ForceSleepTalkAsync(string triggerContent)
        {
            try
            {
                var talk = await sleepTalkCore.GenerateAsync(
                    CurrentFragment ?? "模糊的梦境",
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

        // ---- 片段准备（Prepare：读 DB + 声明记忆占用） ----

        private async Task<FragmentDescriptor?> PrepareFragmentAsync(FragmentType type)
        {
            var cfg = spawnCheck.GetConfig();
            FragmentDescriptor? desc = type switch
            {
                FragmentType.Consolidation => await PrepareConsolidationAsync(cfg),
                FragmentType.Weight => await PrepareWeightAsync(cfg),
                FragmentType.Link => await PrepareLinkAsync(cfg),
                FragmentType.Combine => await PrepareCombineAsync(cfg),
                FragmentType.Dedup => await PrepareDedupAsync(cfg),
                _ => null
            };
            Signal.Event(LogGroup.Engine, desc != null ? "片段准备成功" : "片段准备跳过",
                new { type = type.ToString(), success = desc != null });
            return desc;
        }

        private async Task<FragmentDescriptor?> PrepareConsolidationAsync(DreamConfig cfg)
        {
            var temps = await ctx.TempMemories.GetAllAsync();
            if (temps.Count == 0) { Signal.Event(LogGroup.Engine, "Prepare失败:Consolidation", new { reason = "无临时记忆" }); return null; }

            var batches = BuildBatches(temps, cfg.ConsolidationBatchSize, cfg.ConsolidationSmallGroupThreshold);
            var payload = new ConsolidationPayload { Batches = batches, AllTemps = temps };

            return new FragmentDescriptor
            {
                Type = FragmentType.Consolidation,
                ResourceCost = cfg.ConsolidationResourceCost,
                EstimatedTokens = cfg.ConsolidationTokenEstimate,
                ClaimedMemoryIds = new HashSet<int>(), // 临时库不追踪
                Payload = payload
            };
        }

        private async Task<FragmentDescriptor?> PrepareWeightAsync(DreamConfig cfg)
        {
            var batchSize = cfg.WeightBatchSize;
            var batch = await ctx.Memories.GetUndreamedAsync(batchSize);
            if (batch.Count < batchSize / 2)
                batch.AddRange(await ctx.Memories.GetOldestDreamedAsync(batchSize - batch.Count));
            if (batch.Count == 0) { Signal.Event(LogGroup.Engine, "Prepare失败:Weight", new { reason = "无undreamed或oldest-dreamed记忆" }); return null; }

            Signal.Event(LogGroup.Engine, "Prepare成功:Weight", new { batchSize = batch.Count, memoryIds = batch.Select(m => m.Id).ToList() });
            return new FragmentDescriptor
            {
                Type = FragmentType.Weight,
                ResourceCost = cfg.WeightResourceCost,
                EstimatedTokens = cfg.WeightTokenEstimate,
                ClaimedMemoryIds = batch.Select(m => m.Id).ToHashSet(),
                Payload = new WeightPayload { Batch = batch }
            };
        }

        private async Task<FragmentDescriptor?> PrepareLinkAsync(DreamConfig cfg)
        {
            var targets = await ctx.Memories.GetUndreamedAsync(1);
            if (targets.Count == 0) targets = await ctx.Memories.GetOldestDreamedAsync(1);
            if (targets.Count == 0) { Signal.Event(LogGroup.Engine, "Prepare失败:Link", new { reason = "无undreamed或oldest-dreamed目标" }); return null; }

            var target = targets[0];
            List<MemoryEntry> filtered;
            if (target.Embedding != null)
                filtered = await ctx.Memories.FindSimilarAsync(
                    target.Embedding, cfg.LinkTopK, cfg.LinkCosineThreshold, excludeId: target.Id);
            else
            {
                var candidates = await ctx.Memories.GetRecentAsync(cfg.LinkCandidatePoolSize);
                filtered = candidates.Where(c => c.Id != target.Id).Take(cfg.LinkTopK).ToList();
            }
            if (filtered.Count == 0)
            {
                // 无候选但仍有意义标记为 dreamed
                target.LastDreamTime = DateTime.Now;
                await ctx.Memories.UpdateAsync(target);
                Signal.Event(LogGroup.Engine, "Prepare跳过:Link", new { reason = "无相似候选", targetId = target.Id });
                return null;
            }

            var claimed = new HashSet<int> { target.Id };
            foreach (var f in filtered) claimed.Add(f.Id);

            return new FragmentDescriptor
            {
                Type = FragmentType.Link,
                ResourceCost = cfg.LinkResourceCost,
                EstimatedTokens = cfg.LinkTokenEstimate,
                ClaimedMemoryIds = claimed,
                Payload = new LinkPayload { Target = target, Candidates = filtered }
            };
        }

        private async Task<FragmentDescriptor?> PrepareCombineAsync(DreamConfig cfg)
        {
            var recent = await ctx.Memories.GetRecentAsync(cfg.CombineRecentPoolSize);
            if (recent.Count < 2) { Signal.Event(LogGroup.Engine, "Prepare失败:Combine", new { reason = "近期记忆不足", recentCount = recent.Count }); return null; }
            var ids = recent.Select(m => m.Id).ToList();
            var links = await ctx.MemoryLinks.GetLinksForAsync(ids, cfg.CombineStrengthThreshold);
            if (links.Count == 0) { Signal.Event(LogGroup.Engine, "Prepare失败:Combine", new { reason = "无强关联", recentCount = recent.Count }); return null; }

            MemoryEntry? src = null, tgt = null;
            string? hash = null;
            foreach (var pair in links.OrderByDescending(l => l.Relevance))
            {
                src = recent.FirstOrDefault(m => m.Id == pair.SourceId);
                tgt = recent.FirstOrDefault(m => m.Id == pair.TargetId);
                if (src == null || tgt == null) { src = null; tgt = null; continue; }
                var sids = new List<int> { src.Id, tgt.Id }; sids.Sort();
                hash = ComputeHash(string.Join(",", sids));
                if (await ctx.Memories.GetBySourceHashAsync(hash) != null) { src = null; tgt = null; continue; }
                break;
            }
            if (src == null || tgt == null) { Signal.Event(LogGroup.Engine, "Prepare失败:Combine", new { reason = "所有强关联对已合并或无可用对", checkedPairs = links.Count }); return null; }

            return new FragmentDescriptor
            {
                Type = FragmentType.Combine,
                ResourceCost = cfg.CombineResourceCost,
                EstimatedTokens = cfg.CombineTokenEstimate,
                ClaimedMemoryIds = new HashSet<int> { src.Id, tgt.Id },
                Payload = new CombinePayload { Source = src, Target = tgt, Hash = hash! }
            };
        }

        private async Task<FragmentDescriptor?> PrepareDedupAsync(DreamConfig cfg)
        {
            var minCluster = cfg.DedupMinClusterSize;
            var maxCluster = cfg.DedupClusterSize;

            var seeds = await ctx.Memories.GetUndreamedAsync(3);
            if (seeds.Count == 0) seeds = await ctx.Memories.GetOldestDreamedAsync(3);
            if (seeds.Count == 0) { Signal.Event(LogGroup.Engine, "Prepare失败:Dedup", new { reason = "无undreamed或oldest-dreamed种子" }); return null; }

            // 遍历种子找第一个有效集群
            var processed = new HashSet<int>();
            foreach (var seed in seeds)
            {
                if (processed.Contains(seed.Id)) continue;

                var links = await ctx.MemoryLinks.GetByMemoryIdAsync(seed.Id);
                var linkedIds = links
                    .Select(l => l.SourceId == seed.Id ? l.TargetId : l.SourceId)
                    .Distinct()
                    .Where(id => !processed.Contains(id))
                    .ToList();

                if (linkedIds.Count + 1 < minCluster) continue;

                var clusterIds = new List<int> { seed.Id };
                clusterIds.AddRange(linkedIds.Take(maxCluster - 1));
                var cluster = await ctx.Memories.GetByIdsAsync(clusterIds);
                if (cluster.Count < minCluster) continue;

                return new FragmentDescriptor
                {
                    Type = FragmentType.Dedup,
                    ResourceCost = cfg.DedupResourceCost,
                    EstimatedTokens = cfg.DedupTokenEstimate,
                    ClaimedMemoryIds = cluster.Select(m => m.Id).ToHashSet(),
                    Payload = new DedupPayload { Cluster = cluster }
                };
            }

            Signal.Event(LogGroup.Engine, "Prepare失败:Dedup", new { reason = "无满足最小集群条件的种子", seedsChecked = seeds.Count, minCluster });
            return null;
        }

        // ---- 片段执行（Execute：LLM + 写 DB） ----

        private async Task<FragmentResult> ExecuteFragmentAsync(FragmentDescriptor desc)
        {
            CurrentFragment = desc.Type.ToString();
            CurrentFragmentStartTime = DateTime.Now;
            currentDetails = new();
            currentInputIds = null;
            currentOutputRaw = null;

            Signal.Event(LogGroup.Engine, "片段开始执行",
                new { type = desc.Type.ToString(), resourceCost = desc.ResourceCost, estTokens = desc.EstimatedTokens, claimedMemIds = desc.ClaimedMemoryIds.Count });

            try
            {
                var summary = desc.Type switch
                {
                    FragmentType.Consolidation => await ExecuteConsolidationAsync((ConsolidationPayload)desc.Payload!),
                    FragmentType.Weight => await ExecuteWeightAsync((WeightPayload)desc.Payload!),
                    FragmentType.Link => await ExecuteLinkAsync((LinkPayload)desc.Payload!),
                    FragmentType.Combine => await ExecuteCombineAsync((CombinePayload)desc.Payload!),
                    FragmentType.Dedup => await ExecuteDedupAsync((DedupPayload)desc.Payload!),
                    _ => null
                };

                return new FragmentResult
                {
                    Descriptor = desc,
                    Summary = summary,
                    Success = true
                };
            }
            catch (Exception ex)
            {
                Signal.Error(LogGroup.Engine, $"片段执行失败 {desc.Type}", new { type = desc.Type.ToString(), error = ex.GetType().Name, message = ex.Message });
                return new FragmentResult
                {
                    Descriptor = desc,
                    Summary = ex.Message,
                    Success = false
                };
            }
        }

        private async Task<string?> ExecuteConsolidationAsync(ConsolidationPayload p)
        {
            currentInputIds = string.Join(",", p.AllTemps.Select(t => t.Id));
            CurrentInputDescription = $"整合 {p.AllTemps.Count} 条临时记忆，分 {p.Batches.Count} 批处理";

            var candidates = new List<ConsolidationCandidate>();
            var roundOutputs = new List<string>();

            foreach (var batch in p.Batches)
            {
                if (shouldWake) return "中断";
                var result = await consolidationCore.ConsolidateAsync(batch, []);
                roundOutputs.Add(result);
                candidates.AddRange(ParseFirstRoundResult(result, batch));
            }

            if (candidates.Count == 0)
            {
                currentOutputRaw = string.Join("\n---\n", roundOutputs);
                foreach (var t in p.AllTemps)
                    await ctx.TempMemories.DeleteAsync(t);
                return "无候选，跳过";
            }

            if (shouldWake) return "中断";

            var existing = await ctx.Memories.GetRecentAsync(30);
            var finalResult = await consolidationFinalCore.FinalizeAsync(candidates, existing);
            roundOutputs.Add("=== FINAL ===");
            roundOutputs.Add(finalResult);
            currentOutputRaw = string.Join("\n---\n", roundOutputs);

            await ApplyFinalResult(finalResult, candidates);

            foreach (var t in p.AllTemps)
                await ctx.TempMemories.DeleteAsync(t);

            return $"整合完成：{candidates.Count} 候选，{p.AllTemps.Count} 条临时记忆已清空";
        }

        private async Task<string?> ExecuteWeightAsync(WeightPayload p)
        {
            var batch = p.Batch;
            currentInputIds = string.Join(",", batch.Select(m => m.Id));
            CurrentInputDescription = $"评估 {batch.Count} 条记忆权重: " +
                string.Join("; ", batch.Take(3).Select(m => $"#{m.Id} {(m.Content.Length > 20 ? m.Content[..20] + "…" : m.Content)}")) +
                (batch.Count > 3 ? $" 等{batch.Count}条" : "");

            var result = await weightCore.EvaluateAsync(batch);
            currentOutputRaw = result;
            int adjusted = 0;
            try
            {
                var evals = JArray.Parse(TextUtil.StripMarkdownCodeFence(result));
                foreach (var item in evals)
                {
                    var idx = item["index"]?.Value<int>() ?? -1;
                    var imp = item["importance"]?.Value<float>() ?? -1;
                    var cert = item["certainty"]?.Value<float>();
                    if (idx < 0 || idx >= batch.Count || imp < 0) continue;
                    var m = batch[idx];
                    var oldImp = m.Importance;
                    var oldCert = m.Certainty;
                    m.Importance = Math.Clamp(imp, 0f, 1f);
                    if (cert.HasValue) m.Certainty = Math.Clamp(cert.Value, 0f, 1f);
                    m.LastDreamTime = DateTime.Now;
                    if (imp <= 0.05f) { m.IsPersistent = false; m.ExpiresAt = DateTime.Now.AddDays(7); }
                    await ctx.Memories.UpdateAsync(m);
                    adjusted++;
                    currentDetails.Add(new FragmentDetailRecord
                    {
                        Action = "weight_adjust",
                        MemoryId = m.Id,
                        OldValue = oldImp.ToString("F2"),
                        NewValue = m.Importance.ToString("F2"),
                        Note = m.Content.Length > 50 ? m.Content[..50] : m.Content
                    });
                    if (cert.HasValue)
                        currentDetails.Add(new FragmentDetailRecord
                        {
                            Action = "certainty_adjust",
                            MemoryId = m.Id,
                            OldValue = oldCert.ToString("F2"),
                            NewValue = m.Certainty.ToString("F2"),
                            Note = "LLM评估"
                        });
                }
            }
            catch (Exception ex) { Signal.Warn(LogGroup.Engine, "权重评估解析失败", new { error = ex.Message }); }
            return $"评估{batch.Count}条，调整{adjusted}条";
        }

        private async Task<string?> ExecuteLinkAsync(LinkPayload p)
        {
            var target = p.Target;
            var filtered = p.Candidates;

            currentInputIds = $"{target.Id}:{string.Join(",", filtered.Select(f => f.Id))}";
            CurrentInputDescription = $"分析 #{target.Id} 与 {filtered.Count} 个候选的关联: {(target.Content.Length > 30 ? target.Content[..30] + "…" : target.Content)}";

            var result = await linkCore.AnalyzeLinksAsync(target, filtered);
            currentOutputRaw = result;
            int linksCreated = 0;
            try
            {
                var links = JArray.Parse(TextUtil.StripMarkdownCodeFence(result));
                foreach (var item in links)
                {
                    var ci = item["candidateIndex"]?.Value<int>() ?? -1;
                    var lt = item["linkType"]?.Value<string>() ?? "semantic";
                    var rel = item["relevance"]?.Value<float>() ?? 0f;
                    var sup = item["support"]?.Value<float>() ?? 1.0f;
                    if (ci >= 0 && ci < filtered.Count && rel >= 0.3f)
                    {
                        await ctx.MemoryLinks.CreateOrUpdateAsync(target.Id, filtered[ci].Id, rel, lt, sup);
                        linksCreated++;
                        currentDetails.Add(new FragmentDetailRecord
                        {
                            Action = "link_create",
                            MemoryId = target.Id,
                            Note = $"→#{filtered[ci].Id}, type={lt}, relevance={rel:F2}, support={sup:F2}"
                        });
                    }
                }
            }
            catch (Exception ex) { Signal.Warn(LogGroup.Engine, "关联分析解析失败", new { targetId = target.Id, error = ex.Message }); }
            target.LastDreamTime = DateTime.Now;
            await ctx.Memories.UpdateAsync(target);
            return $"#{target.Id} 建立{linksCreated}个关联";
        }

        private async Task<string?> ExecuteCombineAsync(CombinePayload p)
        {
            var src = p.Source;
            var tgt = p.Target;

            currentInputIds = $"{src.Id},{tgt.Id}";
            CurrentInputDescription = $"组合 #{src.Id}「{(src.Content.Length > 20 ? src.Content[..20] + "…" : src.Content)}」+ #{tgt.Id}「{(tgt.Content.Length > 20 ? tgt.Content[..20] + "…" : tgt.Content)}」";

            var result = await combineCore.CombineAsync([src, tgt]);
            currentOutputRaw = result;
            if (result.Trim().Equals("none", StringComparison.OrdinalIgnoreCase))
                return $"#{src.Id}+#{tgt.Id} 无有价值组合";

            byte[]? emb = null;
            try { emb = VectorUtil.FloatsToBytes(await ctx.Embedding.GetEmbeddingAsync(result)); } catch { }
            await ctx.Memories.CreateDerivedAsync(result, emb,
                System.Text.Json.JsonSerializer.Serialize(new List<int> { src.Id, tgt.Id }),
                p.Hash, src.PersonId ?? tgt.PersonId, src.ChannelId ?? tgt.ChannelId);
            currentDetails.Add(new FragmentDetailRecord
            {
                Action = "combine_derive",
                MemoryId = src.Id,
                Note = $"#{src.Id}+#{tgt.Id} → {(result.Length > 60 ? result[..60] : result)}"
            });
            return $"#{src.Id}+#{tgt.Id} → 衍生记忆";
        }

        private async Task<string?> ExecuteDedupAsync(DedupPayload p)
        {
            var cluster = p.Cluster;
            var cfg = spawnCheck.GetConfig();
            var seed = cluster[0];

            CurrentInputDescription = $"去重集群: #{seed.Id} + {cluster.Count - 1} 条关联记忆";
            var input = $"种子记忆: [{seed.Id}] {seed.Content} (person={seed.PersonId}, importance={seed.Importance:F2})\n\n关联候选:\n";
            for (int i = 1; i < cluster.Count; i++)
            {
                var m = cluster[i];
                input += $"[{i - 1}] {m.Content} (id={m.Id}, person={m.PersonId}, importance={m.Importance:F2})\n";
            }

            var result = await dedupCore.DedupAsync(input);
            currentOutputRaw = result;

            int merged = 0, discarded = 0;
            try
            {
                var actions = JArray.Parse(TextUtil.StripMarkdownCodeFence(result));
                var roundProcessed = new HashSet<int>();

                foreach (var item in actions)
                {
                    var idx = item["index"]?.Value<int>() ?? -1;
                    var action = item["action"]?.Value<string>() ?? "";
                    if (idx < 0 || idx >= cluster.Count || roundProcessed.Contains(idx)) continue;
                    roundProcessed.Add(idx);

                    if (action == "merge")
                    {
                        var mergedContent = item["content"]?.Value<string>() ?? cluster[idx].Content;
                        var mergeWith = item["mergeWith"] as JArray;
                        var survivor = cluster[idx];
                        survivor.Content = mergedContent;
                        var maxImp = survivor.Importance;
                        if (mergeWith != null)
                        {
                            foreach (var mi in mergeWith)
                            {
                                var miIdx = mi.Value<int>();
                                if (miIdx >= 0 && miIdx < cluster.Count && miIdx != idx)
                                {
                                    maxImp = Math.Max(maxImp, cluster[miIdx].Importance);
                                    roundProcessed.Add(miIdx);
                                    await RedirectLinksAsync(cluster[miIdx].Id, survivor.Id);
                                    await ctx.Memories.DeleteAsync(cluster[miIdx]);
                                    merged++;
                                }
                            }
                        }
                        survivor.Importance = maxImp;
                        survivor.LastDreamTime = DateTime.Now;
                        await ctx.Memories.UpdateAsync(survivor);
                        currentDetails.Add(new FragmentDetailRecord
                        {
                            Action = "dedup_merge",
                            MemoryId = survivor.Id,
                            Note = mergedContent.Length > 50 ? mergedContent[..50] : mergedContent
                        });
                    }
                    else if (action == "discard")
                    {
                        await ctx.MemoryLinks.DeleteOrphanedForMemoryAsync(cluster[idx].Id);
                        await ctx.Memories.DeleteAsync(cluster[idx]);
                        discarded++;
                        currentDetails.Add(new FragmentDetailRecord
                        {
                            Action = "dedup_discard",
                            MemoryId = cluster[idx].Id,
                            Note = cluster[idx].Content.Length > 50 ? cluster[idx].Content[..50] : cluster[idx].Content
                        });
                    }
                }
            }
            catch (Exception ex) { Signal.Warn(LogGroup.Engine, "去重解析失败", new { seedId = seed.Id, error = ex.Message }); }

            // 标记参与记忆为 dreamed
            foreach (var m in cluster)
            {
                m.LastDreamTime = DateTime.Now;
                await ctx.Memories.UpdateAsync(m);
            }

            return $"去重集群: 合并={merged}, 丢弃={discarded}";
        }

        // ---- 辅助方法（不变） ----

        private static List<List<TempMemoryEntry>> BuildBatches(
            List<TempMemoryEntry> temps, int batchSize, int smallThreshold)
        {
            var groups = temps.GroupBy(t => t.Subject ?? "misc")
                .ToDictionary(g => g.Key, g => g.ToList());
            var largeBatches = new List<List<TempMemoryEntry>>();
            var miscPool = new List<TempMemoryEntry>();

            foreach (var (subject, entries) in groups)
            {
                if (entries.Count < smallThreshold)
                    miscPool.AddRange(entries);
                else if (entries.Count <= batchSize)
                    largeBatches.Add(entries);
                else
                {
                    var numBatches = (int)Math.Ceiling((double)entries.Count / batchSize);
                    var perBatch = (int)Math.Ceiling((double)entries.Count / numBatches);
                    for (int i = 0; i < entries.Count; i += perBatch)
                        largeBatches.Add(entries.GetRange(i, Math.Min(perBatch, entries.Count - i)));
                }
            }

            if (miscPool.Count > 0)
            {
                if (miscPool.Count <= batchSize)
                    largeBatches.Add(miscPool);
                else
                {
                    var numBatches = (int)Math.Ceiling((double)miscPool.Count / batchSize);
                    var perBatch = (int)Math.Ceiling((double)miscPool.Count / numBatches);
                    for (int i = 0; i < miscPool.Count; i += perBatch)
                        largeBatches.Add(miscPool.GetRange(i, Math.Min(perBatch, miscPool.Count - i)));
                }
            }

            return largeBatches;
        }

        private static List<ConsolidationCandidate> ParseFirstRoundResult(
            string result, List<TempMemoryEntry> batch)
        {
            var candidates = new List<ConsolidationCandidate>();
            try
            {
                var actions = JArray.Parse(TextUtil.StripMarkdownCodeFence(result));
                var processed = new HashSet<int>();
                foreach (var item in actions)
                {
                    var index = item["index"]?.Value<int>() ?? -1;
                    var action = item["action"]?.Value<string>() ?? "";
                    if (index < 0 || index >= batch.Count || processed.Contains(index)) continue;
                    processed.Add(index);
                    var temp = batch[index];

                    switch (action)
                    {
                        case "keep":
                            candidates.Add(new ConsolidationCandidate
                            {
                                Content = temp.Content, PersonId = temp.PersonId,
                                ChannelId = temp.ChannelId, Type = temp.Type,
                                Subject = temp.Subject, Certainty = TempConfidenceToFloat(temp.Confidence)
                            });
                            break;
                        case "merge":
                            var content = item["content"]?.Value<string>() ?? temp.Content;
                            candidates.Add(new ConsolidationCandidate
                            {
                                Content = content, PersonId = temp.PersonId,
                                ChannelId = temp.ChannelId, Type = temp.Type,
                                Subject = temp.Subject, Certainty = TempConfidenceToFloat(temp.Confidence)
                            });
                            var mergeWith = item["mergeWith"] as JArray;
                            if (mergeWith != null)
                                foreach (var mi in mergeWith)
                                    processed.Add(mi.Value<int>());
                            break;
                    }
                }
                for (int i = 0; i < batch.Count; i++)
                {
                    if (!processed.Contains(i))
                    {
                        var temp = batch[i];
                        candidates.Add(new ConsolidationCandidate
                        {
                            Content = temp.Content, PersonId = temp.PersonId,
                            ChannelId = temp.ChannelId, Type = temp.Type,
                            Subject = temp.Subject, Certainty = TempConfidenceToFloat(temp.Confidence)
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Signal.Warn(LogGroup.Engine, "整合初筛解析失败", new { error = ex.GetType().Name, message = ex.Message });
            }
            return candidates;
        }

        private async Task ApplyFinalResult(string result, List<ConsolidationCandidate> candidates)
        {
            try
            {
                var actions = JArray.Parse(TextUtil.StripMarkdownCodeFence(result));
                var processed = new HashSet<int>();
                foreach (var item in actions)
                {
                    var index = item["index"]?.Value<int>() ?? -1;
                    var action = item["action"]?.Value<string>() ?? "";
                    if (index < 0 || index >= candidates.Count || processed.Contains(index)) continue;
                    processed.Add(index);

                    switch (action)
                    {
                        case "keep":
                            var c = candidates[index];
                            byte[]? emb = null;
                            try { emb = VectorUtil.FloatsToBytes(await ctx.Embedding.GetEmbeddingAsync(c.Content)); }
                            catch { }
                            await ctx.Memories.CreateAsync(c.Content, emb, c.PersonId, c.ChannelId,
                                certainty: c.Certainty, type: c.Type ?? MemoryType.Fact, subject: c.Subject);
                            break;
                        case "merge":
                            var content = item["content"]?.Value<string>() ?? candidates[index].Content;
                            var mc = candidates[index];
                            byte[]? memb = null;
                            try { memb = VectorUtil.FloatsToBytes(await ctx.Embedding.GetEmbeddingAsync(content)); }
                            catch { }
                            await ctx.Memories.CreateAsync(content, memb, mc.PersonId, mc.ChannelId,
                                certainty: mc.Certainty, type: mc.Type ?? MemoryType.Fact, subject: mc.Subject);
                            var mergeWith = item["mergeWith"] as JArray;
                            if (mergeWith != null)
                                foreach (var mi in mergeWith)
                                    processed.Add(mi.Value<int>());
                            break;
                    }
                }
                for (int i = 0; i < candidates.Count; i++)
                {
                    if (!processed.Contains(i))
                    {
                        var c = candidates[i];
                        byte[]? emb = null;
                        try { emb = VectorUtil.FloatsToBytes(await ctx.Embedding.GetEmbeddingAsync(c.Content)); }
                        catch { }
                        await ctx.Memories.CreateAsync(c.Content, emb, c.PersonId, c.ChannelId,
                            certainty: c.Certainty, type: c.Type ?? MemoryType.Fact, subject: c.Subject);
                    }
                }
            }
            catch (Exception ex)
            {
                Signal.Error(LogGroup.Engine, "整合入库失败", new { error = ex.GetType().Name, message = ex.Message });
            }
        }

        // ---- 信任评估（纯框架逻辑，不变） ----

        private static ReviewConfig? _cachedReviewConfig;

        private async Task ExecuteTrustEvaluationAsync()
        {
            try
            {
                _cachedReviewConfig ??= ReviewConfig.Load(
                    System.IO.Path.Combine(Config.PathConfig.StoragePath, "Dream", "ReviewConfig.json"));
                var reviewCfg = _cachedReviewConfig;
                var persons = await ctx.Session.GetAllPersonsAsync();

                foreach (var person in persons)
                {
                    bool changed = false;
                    var scores = await ctx.EvaluationScores.GetByTargetAsync("person", person.Id);
                    var dimValues = scores.ToDictionary(s => s.Dimension, s => s.Value);

                    if (person.TrustLevel == TrustLevel.Unknown)
                    {
                        var msgCount = await ctx.Session.GetMessageCountByPersonAsync(person.Id);
                        if (msgCount >= reviewCfg.StrangerMinMessages)
                        { person.TrustLevel = TrustLevel.Stranger; changed = true; }
                    }
                    else if (person.TrustLevel == TrustLevel.Stranger)
                    {
                        var memCount = (await ctx.Memories.GetByPersonAsync(person.Id)).Count;
                        var interactionDays = await ctx.Session.GetInteractionDaysAsync(person.Id);
                        var hardMet = memCount >= reviewCfg.UnderstandingMinMemories
                            && interactionDays >= reviewCfg.UnderstandingMinDays;
                        var anyDimMet = dimValues.Values.Any(v => v >= reviewCfg.UnderstandingAnyDimension);
                        if (hardMet && anyDimMet)
                        { person.TrustLevel = TrustLevel.Understanding; changed = true; }
                        else if (hardMet && !anyDimMet)
                            await ctx.ReviewHints.CreateAsync(
                                $"P#{person.Id} 满足 Understanding 硬性条件但维度未达标，需要评估",
                                person.Id, null, null, "framework");
                    }
                    else if (person.TrustLevel == TrustLevel.Understanding)
                    {
                        var interactionDays = await ctx.Session.GetInteractionDaysAsync(person.Id);
                        var hardMet = interactionDays >= reviewCfg.FamiliarityMinDays && person.AlertLevel == 0;
                        var qualifiedDims = dimValues.Count(kv => kv.Value >= reviewCfg.FamiliarityMajorityDimension);
                        if (hardMet && qualifiedDims >= 3)
                        { person.TrustLevel = TrustLevel.Familiarity; changed = true; }
                        else if (hardMet && qualifiedDims < 3)
                            await ctx.ReviewHints.CreateAsync(
                                $"P#{person.Id} 满足 Familiarity 硬性条件但维度未达标（{qualifiedDims}/3），需要评估",
                                person.Id, null, null, "framework");
                    }
                    else if (person.TrustLevel == TrustLevel.Familiarity)
                    {
                        var interactionDays = await ctx.Session.GetInteractionDaysAsync(person.Id);
                        var reviewCount = await ctx.ReviewLogs.GetSessionCountAsync();
                        var noRecentAlert = person.AlertLevel == 0
                            && (person.LastAlertTime == null
                                || (DateTime.Now - person.LastAlertTime.Value).TotalDays >= 30);
                        var hardMet = interactionDays >= reviewCfg.TrustMinDays
                            && noRecentAlert && reviewCount >= reviewCfg.TrustMinReviewCount;
                        var allDimMet = dimValues.Count >= 4
                            && dimValues.Values.All(v => v >= reviewCfg.TrustAllDimensions);
                        if (hardMet && allDimMet)
                        { person.TrustLevel = TrustLevel.Trust; changed = true; }
                        else if (hardMet && !allDimMet)
                            await ctx.ReviewHints.CreateAsync(
                                $"P#{person.Id} 满足 Trust 硬性条件但维度未达标，需要评估",
                                person.Id, null, null, "framework");
                    }

                    // 降级
                    if (person.TrustLevel == TrustLevel.Trust)
                    {
                        var allAbove = dimValues.Count >= 4
                            && dimValues.Values.All(v => v >= reviewCfg.TrustAllDimensions);
                        if (!allAbove && dimValues.Count >= 4)
                        { person.TrustLevel = TrustLevel.Familiarity; changed = true; }
                    }
                    else if (person.TrustLevel == TrustLevel.Familiarity)
                    {
                        var qualifiedDims = dimValues.Count(kv => kv.Value >= reviewCfg.FamiliarityMajorityDimension);
                        if (qualifiedDims < 3 && dimValues.Count >= 4)
                        { person.TrustLevel = TrustLevel.Understanding; changed = true; }
                    }

                    // 警报冷却
                    if (person.AlertLevel > 0 && person.LastAlertTime != null)
                    {
                        var tcfg = ctx.TrustConfig;
                        var daysSinceAlert = (DateTime.Now - person.LastAlertTime.Value).TotalDays;
                        if (daysSinceAlert >= tcfg.GetAlertCooldownDays(person.AlertLevel))
                        { person.AlertLevel--; changed = true; }
                    }

                    if (changed) await ctx.Session.UpdatePersonAsync(person);
                }
            }
            catch (Exception ex)
            {
                Signal.Warn(LogGroup.Engine, "信任评估失败", new { error = ex.GetType().Name, message = ex.Message });
            }
        }

        // ---- 梦话（不变） ----

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

        // ---- 持久化（不变） ----

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

        /// <summary>临时记忆 Confidence 字符串 → Certainty 浮点桥接</summary>
        private static float TempConfidenceToFloat(string? confidence) => confidence switch
        {
            "high" => 1.0f,
            "low" => 0.3f,
            _ => 0.7f
        };

        private static string ComputeHash(string input)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
            return Convert.ToHexString(bytes)[..16];
        }
    }

    // ---- 片段 Payload 类型 ----

    internal class ConsolidationPayload
    {
        public List<List<TempMemoryEntry>> Batches { get; init; } = new();
        public List<TempMemoryEntry> AllTemps { get; init; } = new();
    }

    internal class WeightPayload
    {
        public List<MemoryEntry> Batch { get; init; } = new();
    }

    internal class LinkPayload
    {
        public MemoryEntry Target { get; init; } = null!;
        public List<MemoryEntry> Candidates { get; init; } = new();
    }

    internal class CombinePayload
    {
        public MemoryEntry Source { get; init; } = null!;
        public MemoryEntry Target { get; init; } = null!;
        public string Hash { get; init; } = "";
    }

    internal class DedupPayload
    {
        public List<MemoryEntry> Cluster { get; init; } = new();
    }

    // 为了兼容旧代码：ConsolidationCandidate 和 FragmentRecord 仍在同名文件中定义（如果不在就加）
    // 这些原来在 DreamEngine.cs 内或其它文件，若编译报错说明在别处
}
