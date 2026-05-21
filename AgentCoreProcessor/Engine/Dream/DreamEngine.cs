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
    /// 走神/小睡使用固定片段数循环，大睡使用两阶段制（浅睡→深睡）。
    /// </summary>
    internal class DreamEngine : ISubEngine
    {
        public string EngineType => "Dream";
        public bool IsAlive { get; private set; } = true;

        private readonly ISystemContext ctx;
        private readonly SleepLevel level;
        private readonly int maxFragments;
        private readonly DreamEngineSpawnCheck spawnCheck;

        private readonly ConsolidationCore consolidationCore = new();
        private readonly ConsolidationFinalCore consolidationFinalCore = new();
        private readonly WeightCore weightCore = new();
        private readonly LinkCore linkCore = new();
        private readonly CombineCore combineCore = new();
        private readonly DedupCore dedupCore = new();
        private readonly SleepTalkCore sleepTalkCore = new();

        private volatile bool shouldWake = false;
        private static readonly Random rng = new();

        // 实时进度（供 WebUI 读取）
        internal string? CurrentFragment { get; private set; }
        internal int FragmentsCompleted { get; private set; }
        internal int FragmentsTotal { get; private set; }
        internal DateTime? CurrentFragmentStartTime { get; private set; }
        internal string? CurrentInputDescription { get; private set; }
        internal FragmentRecord? LastCompletedRecord { get; private set; }

        // 片段执行记录
        private readonly List<FragmentRecord> fragmentRecords = new();
        // 当前片段的详情收集器
        private List<FragmentDetailRecord> currentDetails = new();
        private string? currentInputIds;
        private string? currentOutputRaw;

        /// <summary>每个片段的估算 token 消耗（粗略值，用于预算控制）。</summary>
        private const int EstimatedTokensPerFragment = 2000;

        public DreamEngine(ISystemContext ctx, SleepLevel level, int maxFragments,
            DreamEngineSpawnCheck spawnCheck)
        {
            this.ctx = ctx;
            this.level = level;
            this.maxFragments = maxFragments;
            this.spawnCheck = spawnCheck;
        }

        public async Task RunAsync()
        {
            var parentCtx = AgentCoreProcessor.Logging.SignalContext.Current;
            var lifeCtx = Logging.Signal.Continue(
                parentCtx?.SignalId ?? Logging.Signal.NewId(), parentCtx?.CurrentSpanId,
                "dream:main", Logging.LogGroup.Engine, "Dream引擎",
                new { engineType = EngineType, level = level.ToString() });

            // 清理过期记忆 + 孤立关联（纯机械操作，不消耗模型 token）
            await CleanupExpiredMemoriesAsync();

            ctx.CurrentSleepState = level switch
            {
                SleepLevel.Daydream => SleepState.Daydream,
                SleepLevel.Nap => SleepState.Nap,
                SleepLevel.DeepSleep => SleepState.DeepSleep,
                _ => SleepState.None
            };
            var startTime = DateTime.Now;

            int executed;
            if (level == SleepLevel.DeepSleep)
                executed = await RunDeepSleepAsync();
            else
                executed = await RunLightSleepAsync();

            // 通知 SpawnCheck 更新跨周期状态
            int processed = level == SleepLevel.DeepSleep ? executed : 0;
            spawnCheck.OnDreamCompleted(level, processed);

            // 持久化到数据库
            await PersistSessionAsync(startTime, executed);

            ctx.CurrentSleepState = SleepState.None;
            IsAlive = false;

            lifeCtx.Close(new { engineType = EngineType, reason = "completed", fragments = executed });
        }

        public void OnEvent(EngineEvent e)
        {
            if (e is not MessageEvent msgEvent) return;

            var msg = msgEvent.Message;

            switch (level)
            {
                case SleepLevel.Daydream:
                    // 走神：被 @ 就醒
                    if (msg.IsMentioned)
                    {
                        shouldWake = true;
                    }
                    break;

                case SleepLevel.Nap:
                    // 小睡：关键词叫醒（"起床""醒醒""wake"等）
                    if (msg.IsMentioned && ContainsWakeKeyword(msg.Content))
                    {
                        shouldWake = true;
                    }
                    else if (msg.IsMentioned)
                    {
                        // 仅 @ 不含关键词 → 触发梦话（不打断）
                        _ = ForceSleepTalkAsync(msg.Content);
                    }
                    break;

                case SleepLevel.DeepSleep:
                    // 大睡：仅管理员可叫醒（通过 SignalEvent 处理，不在这里）
                    break;
            }
        }

        /// <summary>强制信号唤醒（管理员/任务桥）。无视 SleepLevel。</summary>
        internal void ForceWake(string reason)
        {
            shouldWake = true;
        }

        private static readonly string[] WakeKeywords =
            ["起床", "醒醒", "wake", "起来", "叫醒", "别睡了", "醒来"];

        private static bool ContainsWakeKeyword(string content)
        {
            var lower = content.ToLowerInvariant();
            return WakeKeywords.Any(k => lower.Contains(k));
        }

        /// <summary>被 @ 但不打断时，强制发一条梦话。</summary>
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

                var targetChannel = channels[rng.Next(channels.Count)];
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

        public void RequestStop() => shouldWake = true;

        // ---- 走神/小睡：固定片段数循环 ----

        private async Task<int> RunLightSleepAsync()
        {
            int executed = 0;
            FragmentsTotal = maxFragments;
            for (int i = 0; i < maxFragments; i++)
            {
                if (shouldWake)
                {
                    Signal.Event(LogGroup.Engine, "睡眠打断", new { level = level.ToString(), fragment = i + 1, total = maxFragments });
                    break;
                }
                var fragment = await SelectFragment(isPhase2: false);
                if (fragment == null) { break; }
                try
                {
                    CurrentFragment = fragment.ToString();
                    CurrentFragmentStartTime = DateTime.Now;
                    currentDetails = new(); currentInputIds = null; currentOutputRaw = null;

                    using var fragSpan = Signal.Open(LogGroup.Engine, $"片段 #{i + 1}/{maxFragments} {fragment}",
                        new { index = i + 1, total = maxFragments, type = fragment.ToString(), level = level.ToString() });

                    var summary = await ExecuteFragment(fragment.Value);
                    var duration = (DateTime.Now - CurrentFragmentStartTime.Value).TotalSeconds;
                    fragmentRecords.Add(new FragmentRecord
                    {
                        Type = fragment.ToString()!,
                        StartTime = CurrentFragmentStartTime.Value,
                        DurationSeconds = duration,
                        Success = true,
                        Summary = summary,
                        InputMemoryIds = currentInputIds,
                        OutputRaw = currentOutputRaw,
                        Details = currentDetails
                    });
                    executed++;
                    FragmentsCompleted = executed;
                    LastCompletedRecord = fragmentRecords[^1];

                    fragSpan.SetCloseDetail(new { success = true, summary, durationSeconds = duration });

                    await MaybeSleepTalkAsync(summary);
                }
                catch (Exception ex)
                {
                    Signal.Error(LogGroup.Engine, $"片段执行失败 #{i + 1} {fragment}", new { type = fragment.ToString(), error = ex.GetType().Name, message = ex.Message });
                    fragmentRecords.Add(new FragmentRecord
                    {
                        Type = fragment.ToString()!,
                        StartTime = CurrentFragmentStartTime ?? DateTime.Now,
                        DurationSeconds = 0,
                        Success = false,
                        Summary = ex.Message,
                        Details = currentDetails
                    });
                }
            }
            CurrentFragment = null;
            return executed;
        }

        // ---- 大睡：两阶段制 ----

        private async Task<int> RunDeepSleepAsync()
        {
            var startTime = DateTime.Now;
            var cfg = spawnCheck.GetConfig();
            int tokensUsed = 0;
            int executed = 0;
            int phase1Budget = cfg.DeepSleepTokenBudget / 3;

            // ========== Phase 1: 浅睡 — 集中清临时记忆 ==========
            Signal.Event(LogGroup.Engine, "大睡 Phase1 开始", new { tokenBudget = phase1Budget, maxMinutes = cfg.DeepSleepMaxMinutes });

            while (!shouldWake)
            {
                if (tokensUsed >= phase1Budget) break;
                if (ElapsedMinutes(startTime) > cfg.DeepSleepMaxMinutes) break;

                var tempCount = (await ctx.TempMemories.GetAllAsync()).Count;
                if (tempCount == 0)
                {
                    break;
                }

                var fragment = await SelectFragment(isPhase2: false);
                if (fragment == null) break;

                try
                {
                    CurrentFragment = fragment.ToString();
                    CurrentFragmentStartTime = DateTime.Now;
                    currentDetails = new();

                    using var fragSpan = Signal.Open(LogGroup.Engine, $"片段 #{executed + 1} {fragment} (P1)",
                        new { index = executed + 1, type = fragment.ToString(), phase = 1, tokensUsed });

                    var summary = await ExecuteFragment(fragment.Value);
                    var duration = (DateTime.Now - CurrentFragmentStartTime.Value).TotalSeconds;
                    fragmentRecords.Add(new FragmentRecord
                    {
                        Type = fragment.ToString()!,
                        StartTime = CurrentFragmentStartTime.Value,
                        DurationSeconds = duration,
                        Success = true,
                        Summary = summary,
                        Details = currentDetails
                    });
                    tokensUsed += EstimatedTokensPerFragment;
                    executed++;
                    FragmentsCompleted = executed;
                    LastCompletedRecord = fragmentRecords[^1];

                    fragSpan.SetCloseDetail(new { success = true, summary, durationSeconds = duration, tokensUsed });

                    await MaybeSleepTalkAsync(summary);
                }
                catch (Exception ex)
                {
                    Signal.Error(LogGroup.Engine, $"片段执行失败 #{executed + 1} {fragment} (P1)", new { error = ex.GetType().Name, message = ex.Message });
                    fragmentRecords.Add(new FragmentRecord
                    {
                        Type = fragment.ToString()!,
                        StartTime = CurrentFragmentStartTime ?? DateTime.Now,
                        DurationSeconds = 0,
                        Success = false,
                        Summary = ex.Message,
                        Details = currentDetails
                    });
                }
            }


            // ========== Phase 2: 深睡 — 信任评估 + 启动 ReviewEngine + 继续做梦 ==========

            if (!shouldWake && ElapsedMinutes(startTime) < cfg.DeepSleepMaxMinutes)
            {
                Signal.Event(LogGroup.Engine, "大睡 Phase2 开始", new { phase1Executed = executed, phase1Tokens = tokensUsed, remainingBudget = cfg.DeepSleepTokenBudget - tokensUsed });

                // 信任等级评估（不消耗 token，纯框架逻辑）
                await ExecuteTrustEvaluationAsync();

                // 启动 ReviewEngine（独立生命周期，不再陪跑）
                try
                {
                    var (mode, preContext, progress) =
                        await ReviewModeSelector.SelectAndPrepareAsync(ctx);
                    var reviewEngine = new ReviewEngine(ctx, mode, preContext, cfg, progress);
                    ctx.StartEngine(reviewEngine);
                    Signal.Event(LogGroup.Engine, "ReviewEngine启动", new { mode = mode.ToString() });
                }
                catch (Exception ex)
                {
                    Signal.Warn(LogGroup.Engine, "ReviewEngine启动失败", new { error = ex.GetType().Name, message = ex.Message });
                }

                // Phase 2 循环：继续跑 Weight/Link/Combine
                int nullCount = 0;
                while (true)
                {
                    if (ElapsedMinutes(startTime) > cfg.DeepSleepMaxMinutes) break;
                    if (shouldWake) break;
                    if (tokensUsed >= cfg.DeepSleepTokenBudget) break;

                    var fragment = await SelectFragment(isPhase2: true);
                    if (fragment != null)
                    {
                        nullCount = 0;
                        try
                        {
                            CurrentFragment = fragment.ToString();
                            CurrentFragmentStartTime = DateTime.Now;
                            currentDetails = new();

                            using var fragSpan = Signal.Open(LogGroup.Engine, $"片段 #{executed + 1} {fragment} (P2)",
                                new { index = executed + 1, type = fragment.ToString(), phase = 2, tokensUsed });

                            var summary = await ExecuteFragment(fragment.Value);
                            var duration = (DateTime.Now - CurrentFragmentStartTime.Value).TotalSeconds;
                            fragmentRecords.Add(new FragmentRecord
                            {
                                Type = fragment.ToString()!,
                                StartTime = CurrentFragmentStartTime.Value,
                                DurationSeconds = duration,
                                Success = true,
                                Summary = summary,
                                Details = currentDetails
                            });
                            tokensUsed += EstimatedTokensPerFragment;
                            executed++;
                            FragmentsCompleted = executed;

                            fragSpan.SetCloseDetail(new { success = true, summary, durationSeconds = duration, tokensUsed });

                            await MaybeSleepTalkAsync(summary);
                        }
                        catch (Exception ex)
                        {
                            Signal.Error(LogGroup.Engine, $"片段执行失败 #{executed + 1} {fragment} (P2)", new { error = ex.GetType().Name, message = ex.Message });
                            fragmentRecords.Add(new FragmentRecord
                            {
                                Type = fragment.ToString()!,
                                StartTime = CurrentFragmentStartTime ?? DateTime.Now,
                                DurationSeconds = 0,
                                Success = false,
                                Summary = ex.Message,
                                Details = currentDetails
                            });
                        }
                    }
                    else
                    {
                        nullCount++;
                        if (nullCount >= 3) break;
                        await Task.Delay(5000);
                    }
                }
            }


            return executed;
        }

        private static double ElapsedMinutes(DateTime startTime)
            => (DateTime.Now - startTime).TotalMinutes;

        // ---- 片段调度 ----

        private async Task<FragmentType?> SelectFragment(bool isPhase2)
        {
            var weights = await ComputeWeights(isPhase2);
            var total = weights.Values.Sum();
            if (total <= 0) return null;
            var roll = rng.NextDouble() * total;
            double cumulative = 0;
            foreach (var (type, weight) in weights)
            {
                cumulative += weight;
                if (roll <= cumulative) return type;
            }
            return weights.Keys.Last();
        }

        private async Task<Dictionary<FragmentType, float>> ComputeWeights(bool isPhase2)
        {
            var weights = new Dictionary<FragmentType, float>();
            var tempCount = (await ctx.TempMemories.GetAllAsync()).Count;

            if (level == SleepLevel.Daydream)
            {
                weights[FragmentType.Weight] = 1.0f;
                weights[FragmentType.Link] = 1.0f;
                return weights;
            }

            if (isPhase2)
            {
                weights[FragmentType.Weight] = 1.0f;
                var undreamed = await ctx.Memories.GetUndreamedAsync(10);
                weights[FragmentType.Link] = undreamed.Count > 0 ? 3.0f : 1.0f;
                weights[FragmentType.Combine] = 0.5f;
                weights[FragmentType.Dedup] = undreamed.Count > 0 ? 3.0f : 0.5f;
            }
            else
            {
                weights[FragmentType.Consolidation] = tempCount > 0 ? 20.0f : 0f;
                weights[FragmentType.Weight] = 1.0f;
                var undreamed = await ctx.Memories.GetUndreamedAsync(10);
                weights[FragmentType.Link] = undreamed.Count > 0 ? 3.0f : 1.0f;
                weights[FragmentType.Combine] = 0.5f;
                weights[FragmentType.Dedup] = undreamed.Count > 0 ? 3.0f : 0.5f;
            }

            return weights;
        }

        private async Task<string?> ExecuteFragment(FragmentType type)
        {
            return type switch
            {
                FragmentType.Consolidation => await ExecuteConsolidationWithSummary(),
                FragmentType.Weight => await ExecuteWeightWithSummary(),
                FragmentType.Link => await ExecuteLinkWithSummary(),
                FragmentType.Combine => await ExecuteCombineWithSummary(),
                FragmentType.Dedup => await ExecuteDedupWithSummary(),
                _ => null
            };
        }

        // ---- 片段执行 ----

        private async Task<string?> ExecuteConsolidationWithSummary()
        {
            await ExecuteConsolidation();
            return null; // 整合自身有详细日志
        }

        private async Task ExecuteConsolidation()
        {
            var temps = await ctx.TempMemories.GetAllAsync();
            if (temps.Count == 0) return;

            var cfg = spawnCheck.GetConfig();
            var batchSize = cfg.ConsolidationBatchSize;
            var smallThreshold = cfg.ConsolidationSmallGroupThreshold;

            currentInputIds = string.Join(",", temps.Select(t => t.Id));

            // ---- 分组 ----
            var batches = BuildBatches(temps, batchSize, smallThreshold);
            CurrentInputDescription = $"整合 {temps.Count} 条临时记忆，分 {batches.Count} 批处理";

            // ---- 第一轮：逐批初筛 ----
            var candidates = new List<ConsolidationCandidate>();
            var roundOutputs = new List<string>();

            foreach (var batch in batches)
            {
                if (shouldWake)
                {
                    return;
                }

                var result = await consolidationCore.ConsolidateAsync(batch, []);
                roundOutputs.Add(result);
                var batchCandidates = ParseFirstRoundResult(result, batch);
                candidates.AddRange(batchCandidates);
            }


            if (candidates.Count == 0)
            {
                currentOutputRaw = string.Join("\n---\n", roundOutputs);
                foreach (var t in temps)
                    await ctx.TempMemories.DeleteAsync(t);
                return;
            }

            // ---- 第二轮：全局精筛 ----
            if (shouldWake)
            {
                return;
            }

            var existing = await ctx.Memories.GetRecentAsync(30);
            var finalResult = await consolidationFinalCore.FinalizeAsync(candidates, existing);
            roundOutputs.Add("=== FINAL ===");
            roundOutputs.Add(finalResult);
            currentOutputRaw = string.Join("\n---\n", roundOutputs);

            // ---- 入库 ----
            await ApplyFinalResult(finalResult, candidates);

            // ---- 清空临时记忆 ----
            foreach (var t in temps)
                await ctx.TempMemories.DeleteAsync(t);

        }

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
                {
                    miscPool.AddRange(entries);
                }
                else if (entries.Count <= batchSize)
                {
                    largeBatches.Add(entries);
                }
                else
                {
                    // 均衡拆分：尽可能少的组，每组尽可能均衡
                    var numBatches = (int)Math.Ceiling((double)entries.Count / batchSize);
                    var perBatch = (int)Math.Ceiling((double)entries.Count / numBatches);
                    for (int i = 0; i < entries.Count; i += perBatch)
                        largeBatches.Add(entries.GetRange(i, Math.Min(perBatch, entries.Count - i)));
                }
            }

            // 杂项池也做均衡拆分
            if (miscPool.Count > 0)
            {
                if (miscPool.Count <= batchSize)
                {
                    largeBatches.Add(miscPool);
                }
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
                                Content = temp.Content,
                                PersonId = temp.PersonId,
                                ChannelId = temp.ChannelId,
                                Type = temp.Type,
                                Subject = temp.Subject,
                                Confidence = temp.Confidence
                            });
                            break;
                        case "merge":
                            var content = item["content"]?.Value<string>() ?? temp.Content;
                            candidates.Add(new ConsolidationCandidate
                            {
                                Content = content,
                                PersonId = temp.PersonId,
                                ChannelId = temp.ChannelId,
                                Type = temp.Type,
                                Subject = temp.Subject,
                                Confidence = temp.Confidence
                            });
                            var mergeWith = item["mergeWith"] as JArray;
                            if (mergeWith != null)
                                foreach (var mi in mergeWith)
                                    processed.Add(mi.Value<int>());
                            break;
                    }
                }
                // 未在输出中出现的 index 默认 keep
                for (int i = 0; i < batch.Count; i++)
                {
                    if (!processed.Contains(i))
                    {
                        var temp = batch[i];
                        candidates.Add(new ConsolidationCandidate
                        {
                            Content = temp.Content,
                            PersonId = temp.PersonId,
                            ChannelId = temp.ChannelId,
                            Type = temp.Type,
                            Subject = temp.Subject,
                            Confidence = temp.Confidence
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
                            await ctx.Memories.CreateAsync(c.Content, emb,
                                c.PersonId, c.ChannelId,
                                confidence: c.Confidence ?? "high",
                                type: c.Type ?? MemoryType.Fact, subject: c.Subject);
                            break;
                        case "merge":
                            var content = item["content"]?.Value<string>() ?? candidates[index].Content;
                            var mc = candidates[index];
                            byte[]? memb = null;
                            try { memb = VectorUtil.FloatsToBytes(await ctx.Embedding.GetEmbeddingAsync(content)); }
                            catch { }
                            await ctx.Memories.CreateAsync(content, memb,
                                mc.PersonId, mc.ChannelId,
                                confidence: mc.Confidence ?? "high",
                                type: mc.Type ?? MemoryType.Fact, subject: mc.Subject);
                            var mergeWith = item["mergeWith"] as JArray;
                            if (mergeWith != null)
                                foreach (var mi in mergeWith)
                                    processed.Add(mi.Value<int>());
                            break;
                    }
                }
                // 未在输出中出现的 index 默认 keep
                for (int i = 0; i < candidates.Count; i++)
                {
                    if (!processed.Contains(i))
                    {
                        var c = candidates[i];
                        byte[]? emb = null;
                        try { emb = VectorUtil.FloatsToBytes(await ctx.Embedding.GetEmbeddingAsync(c.Content)); }
                        catch { }
                        await ctx.Memories.CreateAsync(c.Content, emb,
                            c.PersonId, c.ChannelId,
                            confidence: c.Confidence ?? "high",
                            type: c.Type ?? MemoryType.Fact, subject: c.Subject);
                    }
                }
            }
            catch (Exception ex)
            {
                Signal.Error(LogGroup.Engine, "整合入库失败", new { error = ex.GetType().Name, message = ex.Message });
            }
        }

        private async Task<string?> ExecuteWeightWithSummary()
        {
            var cfg = spawnCheck.GetConfig();
            var batchSize = cfg.WeightBatchSize;
            var batch = await ctx.Memories.GetUndreamedAsync(batchSize);
            if (batch.Count < batchSize / 2) batch.AddRange(await ctx.Memories.GetOldestDreamedAsync(batchSize - batch.Count));
            if (batch.Count == 0) return "无记忆可评估";
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
                    if (idx < 0 || idx >= batch.Count || imp < 0) continue;
                    var m = batch[idx];
                    var oldImp = m.Importance;
                    m.Importance = Math.Clamp(imp, 0f, 1f);
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
                }
            }
            catch (Exception ex) { Signal.Warn(LogGroup.Engine, "权重评估解析失败", new { error = ex.Message }); }
            return $"评估{batch.Count}条，调整{adjusted}条";
        }

        private async Task<string?> ExecuteLinkWithSummary()
        {
            var cfg = spawnCheck.GetConfig();
            var targets = await ctx.Memories.GetUndreamedAsync(cfg.LinkTargetCount);
            if (targets.Count == 0) targets = await ctx.Memories.GetOldestDreamedAsync(cfg.LinkTargetCount);
            if (targets.Count == 0) return "无记忆可关联";
            int linksCreated = 0;
            var inputParts = new List<string>();
            var outputParts = new List<string>();
            CurrentInputDescription = $"关联重建: {targets.Count} 个目标";
            foreach (var target in targets)
            {
                if (shouldWake) break;
                List<MemoryEntry> filtered;
                if (target.Embedding != null)
                {
                    filtered = await ctx.Memories.FindSimilarAsync(
                        target.Embedding, cfg.LinkTopK, cfg.LinkCosineThreshold, excludeId: target.Id);
                }
                else
                {
                    var candidates = await ctx.Memories.GetRecentAsync(cfg.LinkCandidatePoolSize);
                    filtered = candidates.Where(c => c.Id != target.Id).Take(cfg.LinkTopK).ToList();
                }
                if (filtered.Count == 0) { target.LastDreamTime = DateTime.Now; await ctx.Memories.UpdateAsync(target); continue; }
                inputParts.Add($"{target.Id}:{string.Join(",", filtered.Select(f => f.Id))}");
                CurrentInputDescription = $"分析 #{target.Id} 与 {filtered.Count} 个候选的关联: {(target.Content.Length > 30 ? target.Content[..30] + "…" : target.Content)}";                var result = await linkCore.AnalyzeLinksAsync(target, filtered);
                outputParts.Add(result);
                try
                {
                    var links = JArray.Parse(TextUtil.StripMarkdownCodeFence(result));
                    foreach (var item in links)
                    {
                        var ci = item["candidateIndex"]?.Value<int>() ?? -1;
                        var lt = item["linkType"]?.Value<string>() ?? "semantic";
                        var st = item["strength"]?.Value<float>() ?? 0f;
                        if (ci >= 0 && ci < filtered.Count && st >= 0.3f)
                        {
                            await ctx.MemoryLinks.CreateOrUpdateAsync(target.Id, filtered[ci].Id, st, lt);
                            linksCreated++;
                            currentDetails.Add(new FragmentDetailRecord
                            {
                                Action = "link_create",
                                MemoryId = target.Id,
                                Note = $"→#{filtered[ci].Id}, type={lt}, strength={st:F2}"
                            });
                        }
                    }
                }
                catch (Exception ex) { Signal.Warn(LogGroup.Engine, "关联分析解析失败", new { targetId = target.Id, error = ex.Message }); }
                target.LastDreamTime = DateTime.Now;
                await ctx.Memories.UpdateAsync(target);
            }
            currentInputIds = string.Join("|", inputParts);
            currentOutputRaw = string.Join("\n---\n", outputParts);
            return $"分析{targets.Count}条，建立{linksCreated}个关联";
        }

        private async Task<string?> ExecuteCombineWithSummary()
        {
            var cfg = spawnCheck.GetConfig();
            var recent = await ctx.Memories.GetRecentAsync(cfg.CombineRecentPoolSize);
            if (recent.Count < 2) return "记忆不足";
            var ids = recent.Select(m => m.Id).ToList();
            var links = await ctx.MemoryLinks.GetLinksForAsync(ids, cfg.CombineStrengthThreshold);
            if (links.Count == 0) return "无强关联";

            int derived = 0;
            var topPairs = links.OrderByDescending(l => l.Strength).Take(cfg.CombineMaxPairs).ToList();
            currentInputIds = string.Join("|", topPairs.Select(p => $"{p.SourceId},{p.TargetId}"));
            CurrentInputDescription = $"尝试组合 {topPairs.Count} 对强关联记忆";
            var outputParts = new List<string>();
            foreach (var pair in topPairs)
            {
                if (shouldWake) break;
                var src = recent.FirstOrDefault(m => m.Id == pair.SourceId);
                var tgt = recent.FirstOrDefault(m => m.Id == pair.TargetId);
                if (src == null || tgt == null) continue;
                var sids = new List<int> { src.Id, tgt.Id }; sids.Sort();
                var hash = ComputeHash(string.Join(",", sids));
                if (await ctx.Memories.GetBySourceHashAsync(hash) != null) continue;
                var result = await combineCore.CombineAsync([src, tgt]);
                outputParts.Add(result);
                if (result.Trim().Equals("none", StringComparison.OrdinalIgnoreCase)) continue;
                byte[]? emb = null;
                try { emb = VectorUtil.FloatsToBytes(await ctx.Embedding.GetEmbeddingAsync(result)); } catch { }
                await ctx.Memories.CreateDerivedAsync(result, emb,
                    System.Text.Json.JsonSerializer.Serialize(sids), hash,
                    src.PersonId ?? tgt.PersonId, src.ChannelId ?? tgt.ChannelId);
                derived++;
                currentDetails.Add(new FragmentDetailRecord
                {
                    Action = "combine_derive",
                    MemoryId = src.Id,
                    Note = $"#{src.Id}+#{tgt.Id} → {(result.Length > 60 ? result[..60] : result)}"
                });
            }
            currentOutputRaw = string.Join("\n---\n", outputParts);
            return derived > 0 ? $"生成{derived}条衍生记忆" : "无有价值组合";
        }

        private static string ComputeHash(string input)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
            return Convert.ToHexString(bytes)[..16];
        }

        // ---- 信任评估（纯框架逻辑，不消耗 token）----

        private async Task ExecuteTrustEvaluationAsync()
        {
            try
            {
                var tcfg = ctx.TrustConfig;
                var persons = await ctx.Session.GetAllPersonsAsync();

                foreach (var person in persons)
                {
                    bool changed = false;

                    // 1. 硬性条件升级检查
                    if (person.TrustLevel == TrustLevel.Stranger)
                    {
                        var memCount = (await ctx.Memories.GetByPersonAsync(person.Id)).Count;
                        if (memCount >= tcfg.UnderstandingMemoryCount
                            && person.TrustProgress >= 0)
                        {
                            person.TrustLevel = TrustLevel.Understanding;
                            changed = true;
                            // 触发 FastMemory 生成提示
                            if (string.IsNullOrEmpty(person.FastMemory))
                                await ctx.ReviewHints.CreateAsync(
                                    $"Person [{person.Id}] 升级为 Understanding，需要生成 FastMemory", person.Id);
                        }
                    }
                    else if (person.TrustLevel == TrustLevel.Understanding)
                    {
                        var daysSinceCreation = (DateTime.Now - person.CreatedAt).TotalDays;
                        if (daysSinceCreation >= tcfg.FamiliarityDays
                            && person.TrustProgress >= 0)
                        {
                            var memCount = (await ctx.Memories.GetByPersonAsync(person.Id)).Count;
                            if (memCount >= tcfg.FamiliarityInteractionCount)
                            {
                                person.TrustLevel = TrustLevel.Familiarity;
                                changed = true;
                            }
                        }
                    }

                    // 2. TrustProgress 压低等级检查
                    if (person.TrustProgress <= tcfg.ProgressForHostile
                        && person.TrustLevel > TrustLevel.Hostile)
                    {
                        person.TrustLevel = TrustLevel.Hostile;
                        changed = true;
                    }
                    else if (person.TrustProgress <= tcfg.ProgressForWary
                        && person.TrustLevel > TrustLevel.Wary)
                    {
                        person.TrustLevel = TrustLevel.Wary;
                        changed = true;
                    }

                    // 3. 警报冷却恢复
                    if (person.AlertLevel > 0 && person.LastAlertTime != null)
                    {
                        var daysSinceAlert = (DateTime.Now - person.LastAlertTime.Value).TotalDays;
                        var requiredDays = tcfg.GetAlertCooldownDays(person.AlertLevel);
                        if (daysSinceAlert >= requiredDays)
                        {
                            person.AlertLevel--;
                            changed = true;
                        }
                    }

                    if (changed)
                        await ctx.Session.UpdatePersonAsync(person);
                }
            }
            catch (Exception ex)
            {
                Signal.Warn(LogGroup.Engine, "信任评估失败", new { error = ex.GetType().Name, message = ex.Message });
            }
        }

        // ---- 梦话 ----

        /// <summary>概率性地说梦话。大睡 25%，小睡 15%，走神不说。</summary>
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
            if (rng.NextDouble() >= chance) return;

            try
            {
                // 找最近活跃的频道
                var channels = await ctx.Session.GetAllChannelsAsync();
                if (channels.Count == 0) return;

                var targetChannel = channels[rng.Next(channels.Count)];
                var parts = targetChannel.Name.Split(':', 2);
                if (parts.Length != 2) return;

                var talk = await sleepTalkCore.GenerateAsync(fragmentSummary);
                if (string.IsNullOrWhiteSpace(talk)) return;

                // 截断保护
                if (talk.Length > 50) talk = talk[..50];

                var platform = parts[0];
                var platformChannelId = parts[1];

                var sentId = await ctx.Adapters.SendMessageAsync(platform, new OutgoingMessage
                {
                    ChannelId = platformChannelId,
                    Content = talk
                });
                await ctx.Session.SaveBotMessageAsync(targetChannel.Id, talk, sentId);

            }
            catch (Exception ex)
            {
                Signal.Warn(LogGroup.Engine, "梦话发送失败(概率)", new { error = ex.GetType().Name, message = ex.Message });
            }
        }

        // ---- 持久化做梦日志 ----

        private async Task PersistSessionAsync(DateTime startTime, int executed)
        {
            try
            {
                var session = await ctx.DreamLogs.CreateSessionAsync(new DreamSession
                {
                    Level = level.ToString(),
                    StartTime = startTime,
                    EndTime = DateTime.Now,
                    FragmentsExecuted = executed,
                    WasInterrupted = shouldWake
                });

                for (int i = 0; i < fragmentRecords.Count; i++)
                {
                    var rec = fragmentRecords[i];
                    var fragment = await ctx.DreamLogs.CreateFragmentAsync(new DreamFragment
                    {
                        SessionId = session.Id,
                        Type = rec.Type,
                        SeqIndex = i,
                        StartTime = rec.StartTime,
                        DurationSeconds = rec.DurationSeconds,
                        Success = rec.Success,
                        Summary = rec.Summary ?? "",
                        InputMemoryIds = rec.InputMemoryIds,
                        OutputRaw = rec.OutputRaw
                    });

                    if (rec.Details.Count > 0)
                    {
                        var details = rec.Details.Select(d => new DreamFragmentDetail
                        {
                            FragmentId = fragment.Id,
                            Action = d.Action,
                            MemoryId = d.MemoryId,
                            OldValue = d.OldValue,
                            NewValue = d.NewValue,
                            Note = d.Note
                        }).ToList();
                        await ctx.DreamLogs.CreateDetailsAsync(details);
                    }
                }
            }
            catch (Exception ex)
            {
                Signal.Warn(LogGroup.Engine, "做梦日志持久化失败", new { error = ex.GetType().Name, message = ex.Message });
            }
        }

        // ---- 去重片段 ----

        private async Task<string?> ExecuteDedupWithSummary()
        {
            var result = await ExecuteDedup();
            return result;
        }

        private async Task<string?> ExecuteDedup()
        {
            var cfg = spawnCheck.GetConfig();
            var minCluster = cfg.DedupMinClusterSize;
            var maxCluster = cfg.DedupClusterSize;

            // 1. 选种子：优先 undreamed，其次 oldest dreamed
            var seeds = await ctx.Memories.GetUndreamedAsync(3);
            if (seeds.Count == 0) seeds = await ctx.Memories.GetOldestDreamedAsync(3);
            if (seeds.Count == 0) return "无种子记忆";

            int merged = 0;
            int discarded = 0;
            var processed = new HashSet<int>();
            var inputParts = new List<string>();
            var outputParts = new List<string>();

            foreach (var seed in seeds)
            {
                if (shouldWake) break;
                if (processed.Contains(seed.Id)) continue;

                // 2. 扩展集群：1-hop 关联
                var links = await ctx.MemoryLinks.GetByMemoryIdAsync(seed.Id);
                var linkedIds = links
                    .Select(l => l.SourceId == seed.Id ? l.TargetId : l.SourceId)
                    .Distinct()
                    .Where(id => !processed.Contains(id))
                    .ToList();

                if (linkedIds.Count + 1 < minCluster) // +1 for seed itself
                {
                    seed.LastDreamTime = DateTime.Now;
                    await ctx.Memories.UpdateAsync(seed);
                    continue;
                }

                // 取种子+前N条关联
                var clusterIds = new List<int> { seed.Id };
                clusterIds.AddRange(linkedIds.Take(maxCluster - 1));
                var cluster = await ctx.Memories.GetByIdsAsync(clusterIds);
                if (cluster.Count < minCluster)
                {
                    seed.LastDreamTime = DateTime.Now;
                    await ctx.Memories.UpdateAsync(seed);
                    continue;
                }

                CurrentInputDescription = $"去重集群: #{seed.Id} + {cluster.Count - 1} 条关联记忆";
                var input = $"种子记忆: [{seed.Id}] {seed.Content} (person={seed.PersonId}, importance={seed.Importance:F2})\n\n关联候选:\n";
                for (int i = 1; i < cluster.Count; i++)
                {
                    var m = cluster[i];
                    input += $"[{i - 1}] {m.Content} (id={m.Id}, person={m.PersonId}, importance={m.Importance:F2})\n";
                }
                inputParts.Add($"#{seed.Id}→{string.Join(",", linkedIds.Take(maxCluster - 1))}");

                // 3. 模型判断
                var result = await dedupCore.DedupAsync(input);
                outputParts.Add(result);

                // 4. 应用决策
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
                            // 取最高 importance
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
                                        // 重定向关联
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
                    // 默认 keep：不处理
                }
                catch (Exception ex)
                {
                    Signal.Warn(LogGroup.Engine, "去重解析失败", new { seedId = seed.Id, error = ex.Message });
                }

                // 标记参与的记忆为 dreamed
                foreach (var m in cluster)
                {
                    if (!processed.Contains(m.Id))
                    {
                        m.LastDreamTime = DateTime.Now;
                        await ctx.Memories.UpdateAsync(m);
                        processed.Add(m.Id);
                    }
                }
            }

            currentInputIds = string.Join("|", inputParts);
            currentOutputRaw = string.Join("\n---\n", outputParts);
            return $"去重集群={seeds.Count}, 合并={merged}, 丢弃={discarded}";
        }

        /// <summary>将指向旧 ID 的 MemoryLink 重定向到幸存者，并清理旧关联。</summary>
        private async Task RedirectLinksAsync(int oldId, int survivorId)
        {
            try
            {
                var links = await ctx.MemoryLinks.GetByMemoryIdAsync(oldId);
                foreach (var link in links)
                {
                    var newSource = link.SourceId == oldId ? survivorId : link.SourceId;
                    var newTarget = link.TargetId == oldId ? survivorId : link.TargetId;
                    if (newSource == newTarget) continue; // 避免自关联
                    await ctx.MemoryLinks.CreateOrUpdateAsync(newSource, newTarget,
                        link.Strength, link.LinkType);
                    await ctx.MemoryLinks.DeleteAsync(link);
                }
            }
            catch (Exception ex) { Signal.Warn(LogGroup.Engine, "关联重定向失败", new { oldId, survivorId, error = ex.Message }); }
        }

        // ---- 过期清理 ----

        private async Task CleanupExpiredMemoriesAsync()
        {
            try
            {
                var expiredCount = await ctx.Memories.DeleteExpiredAsync();
                var orphanedCount = await ctx.MemoryLinks.DeleteOrphanedAsync();
                if (expiredCount > 0 || orphanedCount > 0)
                    Signal.Event(LogGroup.Engine, "过期清理", new { expiredMemories = expiredCount, orphanedLinks = orphanedCount });
            }
            catch (Exception ex)
            {
                Signal.Warn(LogGroup.Engine, "过期清理失败", new { error = ex.GetType().Name, message = ex.Message });
            }
        }
    }
}
