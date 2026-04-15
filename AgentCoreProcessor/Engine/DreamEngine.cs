using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using AgentCoreProcessor.Config;
using AgentCoreProcessor.Core;
using AgentCoreProcessor.Util;
using Newtonsoft.Json.Linq;

namespace AgentCoreProcessor.Engine
{
    internal enum SleepLevel { Daydream, Nap, DeepSleep }
    internal enum FragmentType { Consolidation, Weight, Link, Combine }

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
        private readonly WeightCore weightCore = new();
        private readonly LinkCore linkCore = new();
        private readonly CombineCore combineCore = new();

        private volatile bool shouldWake = false;
        private static readonly Random rng = new();

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
            FrameworkLogger.Log("DreamEngine", $"开始做梦: level={level} max={maxFragments}");

            int executed;
            if (level == SleepLevel.DeepSleep)
                executed = await RunDeepSleepAsync();
            else
                executed = await RunLightSleepAsync();

            // 通知 SpawnCheck 更新跨周期状态
            int processed = level == SleepLevel.DeepSleep ? executed : 0;
            spawnCheck.OnDreamCompleted(level, processed);

            FrameworkLogger.Log("DreamEngine", $"做梦结束: level={level} executed={executed}");
            IsAlive = false;
        }

        public void OnEvent(EngineEvent e)
        {
            if (e is MessageEvent) shouldWake = true;
        }

        public void RequestStop() => shouldWake = true;

        // ---- 走神/小睡：固定片段数循环 ----

        private async Task<int> RunLightSleepAsync()
        {
            int executed = 0;
            for (int i = 0; i < maxFragments; i++)
            {
                if (shouldWake) { FrameworkLogger.Log("DreamEngine", "被叫醒"); break; }
                var fragment = await SelectFragment(isPhase2: false);
                if (fragment == null) { FrameworkLogger.Log("DreamEngine", "无可执行片段"); break; }
                try
                {
                    FrameworkLogger.Log("DreamEngine", $"执行片段: {fragment}");
                    await ExecuteFragment(fragment.Value);
                    executed++;
                }
                catch (Exception ex)
                {
                    FrameworkLogger.Log("DreamEngine", $"片段异常: {fragment} - {ex.Message}");
                }
            }
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
            FrameworkLogger.Log("DreamEngine", "Phase 1: 浅睡开始");

            while (!shouldWake)
            {
                if (tokensUsed >= phase1Budget) break;
                if (ElapsedMinutes(startTime) > cfg.DeepSleepMaxMinutes) break;

                var tempCount = (await ctx.TempMemories.GetAllAsync()).Count;
                if (tempCount == 0)
                {
                    FrameworkLogger.Log("DreamEngine", "临时记忆已清空，Phase 1 完成");
                    break;
                }

                var fragment = await SelectFragment(isPhase2: false);
                if (fragment == null) break;

                try
                {
                    FrameworkLogger.Log("DreamEngine", $"[Phase1] 执行片段: {fragment}");
                    await ExecuteFragment(fragment.Value);
                    tokensUsed += EstimatedTokensPerFragment;
                    executed++;
                }
                catch (Exception ex)
                {
                    FrameworkLogger.Log("DreamEngine", $"[Phase1] 片段异常: {fragment} - {ex.Message}");
                }
            }

            FrameworkLogger.Log("DreamEngine",
                $"Phase 1 结束: executed={executed} tokens≈{tokensUsed}");

            // ========== Phase 2: 深睡 — 启动 ReviewEngine + 继续做梦 ==========
            ISubEngine? reviewEngine = null;

            if (!shouldWake && ElapsedMinutes(startTime) < cfg.DeepSleepMaxMinutes)
            {
                FrameworkLogger.Log("DreamEngine", "Phase 2: 深睡开始");

                try
                {
                    var (mode, preContext, progress) =
                        await ReviewModeSelector.SelectAndPrepareAsync(ctx);
                    reviewEngine = new ReviewEngine(ctx, mode, preContext,
                        cfg.ReviewTokenBudget, cfg.ReviewReserveBudget, progress);
                    ctx.StartEngine(reviewEngine);
                    FrameworkLogger.Log("DreamEngine", $"ReviewEngine 已启动: mode={mode}");
                }
                catch (Exception ex)
                {
                    FrameworkLogger.Log("DreamEngine", $"ReviewEngine 启动失败: {ex.Message}");
                }

                // Phase 2 循环：继续跑 Weight/Link/Combine，陪跑 ReviewEngine
                int nullCount = 0;
                while (true)
                {
                    // 时间超限
                    if (ElapsedMinutes(startTime) > cfg.DeepSleepMaxMinutes)
                    {
                        FrameworkLogger.Log("DreamEngine", "大睡时间超限");
                        reviewEngine?.RequestStop();
                        break;
                    }

                    // shouldWake 时不立刻退——等 ReviewEngine 完成当前轮
                    if (shouldWake && (reviewEngine == null || !reviewEngine.IsAlive))
                        break;

                    // DreamEngine 自己还有预算
                    if (tokensUsed < cfg.DeepSleepTokenBudget)
                    {
                        var fragment = await SelectFragment(isPhase2: true);
                        if (fragment != null)
                        {
                            nullCount = 0;
                            try
                            {
                                FrameworkLogger.Log("DreamEngine", $"[Phase2] 执行片段: {fragment}");
                                await ExecuteFragment(fragment.Value);
                                tokensUsed += EstimatedTokensPerFragment;
                                executed++;
                            }
                            catch (Exception ex)
                            {
                                FrameworkLogger.Log("DreamEngine",
                                    $"[Phase2] 片段异常: {fragment} - {ex.Message}");
                            }
                        }
                        else
                        {
                            nullCount++;
                            if (reviewEngine == null || !reviewEngine.IsAlive)
                            {
                                if (nullCount >= 3)
                                {
                                    FrameworkLogger.Log("DreamEngine", "Phase 2 无片段可跑且 Review 完成");
                                    break;
                                }
                            }
                            await Task.Delay(5000);
                        }
                    }
                    else
                    {
                        // DreamEngine 预算用完，陪跑等 Review
                        if (reviewEngine == null || !reviewEngine.IsAlive)
                            break;
                        await Task.Delay(5000);
                    }
                }

                // 等待 ReviewEngine 完成当前轮（最多 30 秒）
                if (reviewEngine?.IsAlive == true)
                {
                    FrameworkLogger.Log("DreamEngine", "等待 ReviewEngine 完成当前轮...");
                    var waitStart = DateTime.Now;
                    while (reviewEngine.IsAlive && (DateTime.Now - waitStart).TotalSeconds < 30)
                        await Task.Delay(1000);
                }
            }

            FrameworkLogger.Log("DreamEngine",
                $"大睡结束: totalExecuted={executed} tokens≈{tokensUsed} " +
                $"duration={ElapsedMinutes(startTime):F1}min");

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
                // Phase 2：不跑 Consolidation（临时记忆已空，且 ReviewEngine 可能在写入）
                weights[FragmentType.Weight] = 1.0f;
                var undreamed = await ctx.Memories.GetUndreamedAsync(10);
                weights[FragmentType.Link] = undreamed.Count > 0 ? 3.0f : 1.0f;
                weights[FragmentType.Combine] = 0.5f;
            }
            else
            {
                // Phase 1 / 小睡：Consolidation 权重极高
                weights[FragmentType.Consolidation] = tempCount > 0 ? 20.0f : 0f;
                weights[FragmentType.Weight] = 1.0f;
                var undreamed = await ctx.Memories.GetUndreamedAsync(10);
                weights[FragmentType.Link] = undreamed.Count > 0 ? 3.0f : 1.0f;
                weights[FragmentType.Combine] = 0.5f;
            }

            return weights;
        }

        private async Task ExecuteFragment(FragmentType type)
        {
            switch (type)
            {
                case FragmentType.Consolidation: await ExecuteConsolidation(); break;
                case FragmentType.Weight: await ExecuteWeight(); break;
                case FragmentType.Link: await ExecuteLink(); break;
                case FragmentType.Combine: await ExecuteCombine(); break;
            }
        }

        // ---- 片段执行（保持不变）----

        private async Task ExecuteConsolidation()
        {
            var temps = await ctx.TempMemories.GetAllAsync();
            if (temps.Count == 0) return;
            var existing = await ctx.Memories.GetRecentAsync(20);
            var result = await consolidationCore.ConsolidateAsync(temps, existing);
            try
            {
                var actions = JArray.Parse(result);
                var processed = new HashSet<int>();
                foreach (var item in actions)
                {
                    var index = item["index"]?.Value<int>() ?? -1;
                    var action = item["action"]?.Value<string>() ?? "";
                    if (index < 0 || index >= temps.Count) continue;
                    processed.Add(index);
                    var temp = temps[index];
                    switch (action)
                    {
                        case "keep":
                            await ctx.Memories.CreateAsync(temp.Content, temp.Embedding,
                                temp.PersonId, temp.ChannelId, temp.TopicId, temp.SourceMessageId,
                                confidence: temp.Confidence);
                            await ctx.TempMemories.DeleteAsync(temp);
                            break;
                        case "merge":
                            var content = item["content"]?.Value<string>() ?? temp.Content;
                            byte[]? emb = null;
                            try { emb = VectorUtil.FloatsToBytes(await ctx.Embedding.GetEmbeddingAsync(content)); }
                            catch { }
                            // 合并记忆继承最低置信度
                            var mergeConfidence = temp.Confidence;
                            await ctx.Memories.CreateAsync(content, emb,
                                temp.PersonId, temp.ChannelId, temp.TopicId,
                                confidence: mergeConfidence);
                            await ctx.TempMemories.DeleteAsync(temp);
                            var mergeWith = item["mergeWith"] as JArray;
                            if (mergeWith != null)
                                foreach (var mi in mergeWith)
                                {
                                    var mIdx = mi.Value<int>();
                                    if (mIdx >= 0 && mIdx < temps.Count && !processed.Contains(mIdx))
                                    { processed.Add(mIdx); await ctx.TempMemories.DeleteAsync(temps[mIdx]); }
                                }
                            break;
                        case "discard":
                            await ctx.TempMemories.DeleteAsync(temp);
                            break;
                    }
                }
            }
            catch (Exception ex) { FrameworkLogger.Log("DreamEngine", $"整合解析失败: {ex.Message}"); }
        }

        private async Task ExecuteWeight()
        {
            var batch = await ctx.Memories.GetUndreamedAsync(10);
            if (batch.Count < 5) batch.AddRange(await ctx.Memories.GetOldestDreamedAsync(10 - batch.Count));
            if (batch.Count == 0) return;
            var result = await weightCore.EvaluateAsync(batch);
            try
            {
                var evals = JArray.Parse(result);
                foreach (var item in evals)
                {
                    var idx = item["index"]?.Value<int>() ?? -1;
                    var imp = item["importance"]?.Value<float>() ?? -1;
                    if (idx < 0 || idx >= batch.Count || imp < 0) continue;
                    var m = batch[idx];
                    m.Importance = Math.Clamp(imp, 0f, 1f);
                    m.LastDreamTime = DateTime.Now;
                    if (imp <= 0.05f) { m.IsPersistent = false; m.ExpiresAt = DateTime.Now.AddDays(7); }
                    await ctx.Memories.UpdateAsync(m);
                }
            }
            catch (Exception ex) { FrameworkLogger.Log("DreamEngine", $"权重解析失败: {ex.Message}"); }
        }

        private async Task ExecuteLink()
        {
            var targets = await ctx.Memories.GetUndreamedAsync(3);
            if (targets.Count == 0) targets = await ctx.Memories.GetOldestDreamedAsync(3);
            if (targets.Count == 0) return;
            var candidates = await ctx.Memories.GetRecentAsync(20);
            foreach (var target in targets)
            {
                if (shouldWake) break;
                var filtered = candidates.Where(c => c.Id != target.Id).ToList();
                if (filtered.Count == 0) continue;
                if (target.Embedding != null)
                {
                    var tv = VectorUtil.BytesToFloats(target.Embedding);
                    filtered = filtered.Where(c => c.Embedding != null)
                        .Select(c => (e: c, s: VectorUtil.CosineSimilarity(tv, VectorUtil.BytesToFloats(c.Embedding!))))
                        .Where(x => x.s > 0.3f).OrderByDescending(x => x.s).Take(10)
                        .Select(x => x.e).ToList();
                }
                else filtered = filtered.Take(10).ToList();
                if (filtered.Count == 0) { target.LastDreamTime = DateTime.Now; await ctx.Memories.UpdateAsync(target); continue; }
                var result = await linkCore.AnalyzeLinksAsync(target, filtered);
                try
                {
                    var links = JArray.Parse(result);
                    foreach (var item in links)
                    {
                        var ci = item["candidateIndex"]?.Value<int>() ?? -1;
                        var lt = item["linkType"]?.Value<string>() ?? "semantic";
                        var st = item["strength"]?.Value<float>() ?? 0f;
                        if (ci >= 0 && ci < filtered.Count && st >= 0.3f)
                            await ctx.MemoryLinks.CreateOrUpdateAsync(target.Id, filtered[ci].Id, st, lt);
                    }
                }
                catch (Exception ex) { FrameworkLogger.Log("DreamEngine", $"关联解析失败: {ex.Message}"); }
                target.LastDreamTime = DateTime.Now;
                await ctx.Memories.UpdateAsync(target);
            }
        }

        private async Task ExecuteCombine()
        {
            var recent = await ctx.Memories.GetRecentAsync(30);
            if (recent.Count < 2) return;
            var ids = recent.Select(m => m.Id).ToList();
            var links = await ctx.MemoryLinks.GetLinksForAsync(ids, 0.7f);
            if (links.Count == 0) return;
            var best = links.OrderByDescending(l => l.Strength).First();
            var src = recent.FirstOrDefault(m => m.Id == best.SourceId);
            var tgt = recent.FirstOrDefault(m => m.Id == best.TargetId);
            if (src == null || tgt == null) return;
            var sids = new List<int> { src.Id, tgt.Id }; sids.Sort();
            var hash = ComputeHash(string.Join(",", sids));
            if (await ctx.Memories.GetBySourceHashAsync(hash) != null) return;
            var result = await combineCore.CombineAsync([src, tgt]);
            if (result.Trim().Equals("none", StringComparison.OrdinalIgnoreCase)) return;
            byte[]? emb = null;
            try { emb = VectorUtil.FloatsToBytes(await ctx.Embedding.GetEmbeddingAsync(result)); } catch { }
            await ctx.Memories.CreateDerivedAsync(result, emb,
                System.Text.Json.JsonSerializer.Serialize(sids), hash,
                src.PersonId ?? tgt.PersonId, src.ChannelId ?? tgt.ChannelId, src.TopicId ?? tgt.TopicId);
        }

        private static string ComputeHash(string input)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
            return Convert.ToHexString(bytes)[..16];
        }
    }
}
