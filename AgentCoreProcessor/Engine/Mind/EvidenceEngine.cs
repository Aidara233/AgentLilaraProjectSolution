using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AgentCoreProcessor.Database;
using Newtonsoft.Json.Linq;

namespace AgentCoreProcessor.Engine.Mind
{
    /// <summary>
    /// 证据引擎。计算断言的【实际可信度 c_actual】（05 第四节）：
    /// - 证据边 = assertion 槽位引用的支持/反对断言（支持 + 负 strength = 反对）
    /// - 迭代重算：所有 c_actual ← c⁰ 初始化，多轮 noisy-OR + logit/sigmoid，加阻尼，到收敛或轮数上限
    /// - 跨等级碾压：stated 直接压 inferred，与数字无关
    /// - 冲突组内 c_actual 和 ≤1 恒定，超预算标记（供 ConflictService 入队）
    /// </summary>
    internal class EvidenceEngine
    {
        private const int MaxIterations = 20;
        private const double ConvergenceThreshold = 1e-4;
        private const double Damping = 0.85;
        private const double KSup = 2.0;   // 支持调节系数（约 2~4，数值待定见 06）
        private const double KOpp = 2.0;   // 反对调节系数

        private readonly ClaimRepository _claims;
        private readonly ConflictGroupRepository _groups;

        public EvidenceEngine(ClaimRepository claims, ConflictGroupRepository groups)
        {
            _claims = claims;
            _groups = groups;
        }

        /// <summary>
        /// 重算受影响断言的 c_actual。返回哪些冲突组现在超预算（组内和 &gt; 1）。
        /// 全量迭代（规模小，YAGNI；规模大后再做环检测/拓扑序优化）。
        /// </summary>
        public async Task<List<int>> RecomputeAsync(IReadOnlyCollection<int> affectedClaimIds)
        {
            if (affectedClaimIds.Count == 0) return new List<int>();

            // 全量加载（迭代需要全局视角）
            var all = await _claims.GetAllAsync();
            var byId = all.ToDictionary(c => c.Id);

            // 收集受影响闭包：从受影响断言出发，沿支持/反对边扩展（二跳足够）
            var touched = new HashSet<int>(affectedClaimIds);
            ExpandByEdges(all, byId, touched);

            // c_actual 内存表（anchor → 当前值）
            var actual = new Dictionary<int, double>();
            foreach (var c in all)
                actual[c.Id] = c.Credibility;

            // 迭代
            for (int iter = 0; iter < MaxIterations; iter++)
            {
                double maxChange = 0;
                foreach (var c in all)
                {
                    if (!touched.Contains(c.Id)) continue;

                    // 跨等级碾压：stated 直接压 inferred
                    if (!c.IsInference && anyStatedContradicts(c, all, byId))
                    {
                        actual[c.Id] = 1e-6;
                        continue;
                    }

                    // 证据边聚合
                    var (support, oppose) = AggregateEvidence(c, all, byId, actual);
                    var newValue = EvidenceMath.Fuse(c.Credibility, support, oppose, KSup, KOpp);
                    newValue = EvidenceMath.Dampen(newValue, actual[c.Id], Damping);

                    maxChange = Math.Max(maxChange, Math.Abs(newValue - actual[c.Id]));
                    actual[c.Id] = newValue;
                }

                if (maxChange < ConvergenceThreshold) break;
            }

            // 更新库中受影响断言的 c_actual？不落库（c_actual 是派生值，召回时现算）。
            // 这里只返回超预算冲突组 Id。
            return await FindOverBudgetGroupsAsync(all, actual);
        }

        /// <summary>沿支持/反对边扩展受影响闭包（二跳）。</summary>
        private static void ExpandByEdges(List<Claim> all, Dictionary<int, Claim> byId, HashSet<int> touched)
        {
            for (int hop = 0; hop < 2; hop++)
            {
                var added = new List<int>();
                foreach (var c in all)
                {
                    if (c.SubjectJson.Contains("\"assertion\"") || c.ObjectJson.Contains("\"assertion\""))
                    {
                        // 支持/反对边：断言引用断言
                        var subj = ClaimSlot.FromJson(c.SubjectJson);
                        var obj = ClaimSlot.FromJson(c.ObjectJson);
                        if (subj?.Ref.HasValue == true && touched.Contains(subj.Ref.Value) && !touched.Contains(c.Id))
                            added.Add(c.Id);
                        if (obj?.Ref.HasValue == true && touched.Contains(obj.Ref.Value) && !touched.Contains(c.Id))
                            added.Add(c.Id);
                    }
                }
                if (added.Count == 0) break;
                foreach (var id in added) touched.Add(id);
            }
        }

        /// <summary>聚合断言 A 的支持/反对证据。</summary>
        private static (double support, double oppose) AggregateEvidence(
            Claim target, List<Claim> all, Dictionary<int, Claim> byId, Dictionary<int, double> actual)
        {
            var supportStrengths = new List<double>();
            var opposeStrengths = new List<double>();

            foreach (var edge in all)
            {
                if (edge.PredicateJson.Contains("支持"))
                {
                    // 支持/反对边：subject 是 assertion（支持者），object 是 assertion（被支持者）
                    var subj = ClaimSlot.FromJson(edge.SubjectJson);
                    var obj = ClaimSlot.FromJson(edge.ObjectJson);
                    if (subj?.Ref.HasValue != true || obj?.Ref.HasValue != true) continue;
                    if (obj.Ref.Value != target.Id) continue;

                    var supporterId = subj.Ref.Value;
                    if (!byId.ContainsKey(supporterId)) continue;
                    var supporter = byId[supporterId];

                    var cS = actual.TryGetValue(supporterId, out var cs) ? cs : supporter.Credibility;
                    var cC = edge.Credibility;
                    var s = edge.Strength ?? 0;

                    var e = EvidenceMath.EvidenceStrength(cS, cC, s);
                    if (e > 0) supportStrengths.Add(e);
                    else if (e < 0) opposeStrengths.Add(-e);
                }
            }

            var support = EvidenceMath.NoisyOr(supportStrengths);
            var oppose = EvidenceMath.NoisyOr(opposeStrengths);
            return (support, oppose);
        }

        /// <summary>是否有 stated 断言直接否定 inferred 目标（跨等级碾压）。</summary>
        private static bool anyStatedContradicts(Claim target, List<Claim> all, Dictionary<int, Claim> byId)
        {
            if (!target.IsInference) return false;

            foreach (var other in all)
            {
                if (other.Id == target.Id) continue;
                if (other.IsInference) continue;
                if (other.SubjectJson == target.SubjectJson
                    && other.PredicateJson == target.PredicateJson
                    && other.ObjectJson != target.ObjectJson)
                    return true;
            }
            return false;
        }

        /// <summary>计算受影响冲突组的组内和，返回超预算（和 &gt; 1）的组 Id。</summary>
        private async Task<List<int>> FindOverBudgetGroupsAsync(
            List<Claim> all, Dictionary<int, double> actual)
        {
            var overBudget = new List<int>();
            var groups = await _groups.GetActiveAsync();
            foreach (var g in groups)
            {
                var members = await _groups.GetMemberClaimIdsAsync(g.Id);
                double sum = 0;
                foreach (var cid in members)
                {
                    if (actual.TryGetValue(cid, out var c)) sum += c;
                    else
                    {
                        var claim = all.FirstOrDefault(c => c.Id == cid);
                        if (claim != null) sum += claim.Credibility;
                    }
                }
                if (sum > 1.0 + 1e-6) overBudget.Add(g.Id);
            }
            return overBudget;
        }
    }
}
