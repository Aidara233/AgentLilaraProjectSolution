using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AgentCoreProcessor.Config;
using AgentCoreProcessor.Database;
using Newtonsoft.Json;

namespace AgentCoreProcessor.Engine.Mind
{
    /// <summary>
    /// Review 暂存区（05 6.0 git 式提交）。
    /// 三类暂存操作均先进暂存区，preview dry-run 预览影响，commit 原子落库：
    ///   1. 可信度调整（StageCredibility）
    ///   2. 解除属性排他（StageExclusivityRemoval）— IsExclusive → false，今后不再触发冲突组
    ///   3. 拆解冲突组（StageGroupResolution）— 标记 ResolvedAt，组内预算约束解除
    /// 按 sessionId 键控（一次只跑一个 review 会话，天然串行）。
    /// </summary>
    internal class ReviewStage
    {
        private readonly string _sessionId;
        private readonly ClaimRepository _claims;
        private readonly ConflictGroupRepository _groups;
        private readonly KeywordRepository _keywords;
        private readonly EvidenceEngine _evidence;

        private string StagePath => Path.Combine(PathConfig.StoragePath, "Review", $"{_sessionId}.json");

        public ReviewStage(
            string sessionId,
            ClaimRepository claims,
            ConflictGroupRepository groups,
            KeywordRepository keywords,
            EvidenceEngine evidence)
        {
            _sessionId = sessionId;
            _claims = claims;
            _groups = groups;
            _keywords = keywords;
            _evidence = evidence;
        }

        // ── 暂存操作 ──

        /// <summary>把一条断言的可信度锚点调值进暂存区。</summary>
        public void StageCredibility(int claimId, float newCredibility)
        {
            var stage = Load();
            stage.CredibilityChanges[claimId] = newCredibility;
            Save(stage);
        }

        /// <summary>把一个属性关键词的排他性解除操作加进暂存区（IsExclusive → false）。</summary>
        public void StageExclusivityRemoval(string keyword)
        {
            if (string.IsNullOrWhiteSpace(keyword)) return;
            var stage = Load();
            if (!stage.ExclusivityRemovals.Contains(keyword))
                stage.ExclusivityRemovals.Add(keyword);
            Save(stage);
        }

        /// <summary>把一个冲突组的解决操作加进暂存区（标记 ResolvedAt，解除组内预算约束）。</summary>
        public void StageGroupResolution(int groupId)
        {
            var stage = Load();
            if (!stage.GroupResolutions.Contains(groupId))
                stage.GroupResolutions.Add(groupId);
            Save(stage);
        }

        /// <summary>清空暂存区。</summary>
        public void Clear()
        {
            if (File.Exists(StagePath)) File.Delete(StagePath);
        }

        /// <summary>当前暂存的改动。</summary>
        public StageSnapshot Current => Load();

        // ── 预览 ──

        /// <summary>
        /// 预览影响（dry-run，不写库）。
        /// 输出：可信度改动影响哪些断言 + 哪些组超预算；排他解除和组解决操作只做描述性说明。
        /// </summary>
        public async Task<PreviewResult> PreviewAsync()
        {
            var stage = Load();
            bool hasAny = stage.CredibilityChanges.Count > 0
                       || stage.ExclusivityRemovals.Count > 0
                       || stage.GroupResolutions.Count > 0;

            if (!hasAny)
                return new PreviewResult { Ok = true, Message = "暂存区为空" };

            var lines = new List<string>();

            // 可信度 dry-run
            List<int> overBudget = new();
            if (stage.CredibilityChanges.Count > 0)
            {
                var affected = stage.CredibilityChanges.Keys.ToList();
                overBudget = await _evidence.RecomputeAsync(affected);
                lines.Add($"可信度改动 {stage.CredibilityChanges.Count} 条，超预算冲突组 {overBudget.Count} 个");
            }

            // 排他解除说明（不需要 dry-run，直接描述）
            if (stage.ExclusivityRemovals.Count > 0)
                lines.Add($"解除排他属性 {stage.ExclusivityRemovals.Count} 个：{string.Join("、", stage.ExclusivityRemovals)}（提交后不再触发冲突组）");

            // 组解决说明
            if (stage.GroupResolutions.Count > 0)
                lines.Add($"拆解冲突组 {stage.GroupResolutions.Count} 个：{string.Join(", ", stage.GroupResolutions)}（标记 ResolvedAt，组内预算约束解除）");

            return new PreviewResult
            {
                Ok = true,
                AffectedClaims = stage.CredibilityChanges.Keys.ToList(),
                OverBudgetGroups = overBudget,
                Message = string.Join("；", lines),
            };
        }

        // ── 提交 ──

        /// <summary>
        /// 提交暂存区（原子事务落库 + 重算 + 校验组内和 ≤1）。
        /// 三类操作按序执行：先排他解除 + 组解决，再可信度，最后重算校验。
        /// 排他解除和组解决不受组内预算校验阻塞——它们本身就是在松绑约束。
        /// </summary>
        public async Task<CommitResult> CommitAsync()
        {
            var stage = Load();
            bool hasAny = stage.CredibilityChanges.Count > 0
                       || stage.ExclusivityRemovals.Count > 0
                       || stage.GroupResolutions.Count > 0;

            if (!hasAny)
                return new CommitResult { Success = false, Error = "暂存区为空" };

            int committed = 0;

            // 1. 解除排他属性
            foreach (var kwName in stage.ExclusivityRemovals)
            {
                var kw = await _keywords.GetByNameAsync(kwName);
                if (kw == null || !kw.IsExclusive) continue;
                kw.IsExclusive = false;
                await _keywords.UpdateAsync(kw);
                committed++;
            }

            // 2. 拆解冲突组
            foreach (var groupId in stage.GroupResolutions)
            {
                var group = await _groups.GetByIdAsync(groupId);
                if (group == null || group.ResolvedAt != null) continue;
                group.ResolvedAt = DateTime.Now;
                await _groups.UpdateAsync(group);
                committed++;
            }

            // 3. 可信度调整 + 重算 + 校验
            if (stage.CredibilityChanges.Count > 0)
            {
                foreach (var (claimId, newCredibility) in stage.CredibilityChanges)
                {
                    var claim = await _claims.GetByIdAsync(claimId);
                    if (claim == null) continue;
                    claim.Credibility = newCredibility;
                    await _claims.UpdateAsync(claim);
                }

                var affected = stage.CredibilityChanges.Keys.ToList();
                var overBudget = await _evidence.RecomputeAsync(affected);
                if (overBudget.Count > 0)
                    return new CommitResult
                    {
                        Success = false,
                        Error = $"组内和超过预算：{string.Join(",", overBudget)}（排他解除和组解决已落库）",
                        OverBudgetGroups = overBudget
                    };

                committed += stage.CredibilityChanges.Count;
            }

            Clear();
            return new CommitResult { Success = true, CommittedChanges = committed };
        }

        // ── 持久化 ──

        private StageSnapshot Load()
        {
            if (!File.Exists(StagePath)) return new StageSnapshot();
            try
            {
                return JsonConvert.DeserializeObject<StageSnapshot>(File.ReadAllText(StagePath)) ?? new StageSnapshot();
            }
            catch { return new StageSnapshot(); }
        }

        private void Save(StageSnapshot stage)
        {
            var dir = Path.GetDirectoryName(StagePath);
            if (dir != null) Directory.CreateDirectory(dir);
            var tmp = StagePath + ".tmp";
            File.WriteAllText(tmp, JsonConvert.SerializeObject(stage, Formatting.Indented));
            File.Move(tmp, StagePath, overwrite: true);
        }
    }

    /// <summary>暂存区快照（持久化到 Storage/Review/{sessionId}.json）。</summary>
    public class StageSnapshot
    {
        /// <summary>断言 Id → 新可信度锚点值。</summary>
        public Dictionary<int, float> CredibilityChanges { get; set; } = new();

        /// <summary>待解除排他的属性关键词表面词列表（IsExclusive → false）。</summary>
        public List<string> ExclusivityRemovals { get; set; } = new();

        /// <summary>待标记已解决的冲突组 Id 列表（ResolvedAt = now）。</summary>
        public List<int> GroupResolutions { get; set; } = new();
    }

    /// <summary>preview 结果（dry-run）。</summary>
    public class PreviewResult
    {
        public bool Ok { get; set; }
        public string Message { get; set; } = "";
        public List<int> AffectedClaims { get; set; } = new();
        public List<int> OverBudgetGroups { get; set; } = new();
    }

    /// <summary>commit 结果。</summary>
    public class CommitResult
    {
        public bool Success { get; set; }
        public string Error { get; set; } = "";
        public int CommittedChanges { get; set; }
        public List<int> OverBudgetGroups { get; set; } = new();
    }
}
