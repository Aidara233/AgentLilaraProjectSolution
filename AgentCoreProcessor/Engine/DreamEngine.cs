using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using AgentCoreProcessor.Core;
using AgentCoreProcessor.Util;
using Newtonsoft.Json.Linq;

namespace AgentCoreProcessor.Engine
{
    internal enum SleepLevel { Daydream, Nap, DeepSleep }
    internal enum FragmentType { Consolidation, Weight, Link, Combine }

    /// <summary>
    /// 做梦引擎实例。每次睡觉创建，完成后销毁。
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
            int executed = 0;
            FrameworkLogger.Log("DreamEngine", $"开始做梦: level={level} max={maxFragments}");
// PLACEHOLDER_DREAM_RUN
            for (int i = 0; i < maxFragments; i++)
            {
                if (shouldWake) { FrameworkLogger.Log("DreamEngine", "被叫醒"); break; }
                var fragment = await SelectFragment();
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
            // 通知 SpawnCheck 更新跨周期状态
            int processed = 0;
            if (level == SleepLevel.DeepSleep)
                processed = executed; // 简化：用执行片段数近似
            spawnCheck.OnDreamCompleted(level, processed);

            FrameworkLogger.Log("DreamEngine", $"做梦结束: level={level} executed={executed}");
            IsAlive = false;
        }

        public void OnEvent(EngineEvent e)
        {
            if (e is MessageEvent) shouldWake = true;
        }

        public void RequestStop() => shouldWake = true;

        // ---- 片段调度 ----

        private async Task<FragmentType?> SelectFragment()
        {
            var weights = await ComputeWeights();
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

        private async Task<Dictionary<FragmentType, float>> ComputeWeights()
        {
            var weights = new Dictionary<FragmentType, float>();
            var tempCount = (await ctx.TempMemories.GetAllAsync()).Count;
            if (level == SleepLevel.Daydream)
            {
                weights[FragmentType.Weight] = 1.0f;
                weights[FragmentType.Link] = 1.0f;
                return weights;
            }
            weights[FragmentType.Consolidation] = tempCount > 0 ? 10.0f : 0f;
            weights[FragmentType.Weight] = 1.0f;
            var undreamed = await ctx.Memories.GetUndreamedAsync(10);
            weights[FragmentType.Link] = undreamed.Count > 0 ? 3.0f : 1.0f;
            weights[FragmentType.Combine] = 0.5f;
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
                                temp.PersonId, temp.ChannelId, temp.TopicId, temp.SourceMessageId);
                            await ctx.TempMemories.DeleteAsync(temp);
                            break;
                        case "merge":
                            var content = item["content"]?.Value<string>() ?? temp.Content;
                            byte[]? emb = null;
                            try { emb = VectorUtil.FloatsToBytes(await ctx.Embedding.GetEmbeddingAsync(content)); }
                            catch { }
                            await ctx.Memories.CreateAsync(content, emb,
                                temp.PersonId, temp.ChannelId, temp.TopicId);
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
