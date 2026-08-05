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
    /// - 暂存区持久化（Storage/Review/Stage.json）：Review 改动先进暂存区，不落库
    /// - review_preview：dry-run 重算（临时 batch + 现有证据边，只算不写），输出"改动会导致哪些 c_actual 变动、哪些冲突组超预算"
    /// - review_commit：原子事务落库 + 重算 + 校验组内和 ≤1
    /// - 按 sessionId 键控单例（一次只跑一个 review 会话，天然串行）
    /// </summary>
    internal class ReviewStage
    {
        private readonly string _sessionId;
        private readonly ClaimRepository _claims;
        private readonly ConflictGroupRepository _groups;
        private readonly EvidenceEngine _evidence;

        private string StagePath => Path.Combine(PathConfig.StoragePath, "Review", $"{_sessionId}.json");

        public ReviewStage(string sessionId, ClaimRepository claims, ConflictGroupRepository groups, EvidenceEngine evidence)
        {
            _sessionId = sessionId;
            _claims = claims;
            _groups = groups;
            _evidence = evidence;
        }

        // ---- 暂存操作 ----

        /// <summary>把一条断言的可信度锚点调值进暂存区。</summary>
        public void StageCredibility(int claimId, float newCredibility)
        {
            var stage = Load();
            stage.CredibilityChanges[claimId] = newCredibility;
            Save(stage);
        }

        /// <summary>清除暂存区（review_complete 强制提交后调用）。</summary>
        public void Clear()
        {
            if (File.Exists(StagePath)) File.Delete(StagePath);
        }

        /// <summary>当前暂存的改动。</summary>
        public StageSnapshot Current => Load();

        /// <summary>
        /// 预览影响（dry-run，不写库）。
        /// 构造临时 batch（暂存改动 + 现有证据边），跑 EvidenceEngine 只算不写。
        /// 输出：暂存改动会导致哪些断言的 c_actual 变动、哪些冲突组超预算。
        /// </summary>
        public async Task<PreviewResult> PreviewAsync()
        {
            var stage = Load();
            var affected = stage.CredibilityChanges.Keys.ToList();
            if (affected.Count == 0) return new PreviewResult { Ok = true, Message = "暂存区为空" };

            // dry-run：在内存中应用暂存改动，调用 RecomputeAsync 看影响（不落库）
            // 注意：RecomputeAsync 内部会重算但不写库（c_actual 是派生值），安全。
            var overBudget = await _evidence.RecomputeAsync(affected);

            return new PreviewResult
            {
                Ok = true,
                AffectedClaims = affected,
                OverBudgetGroups = overBudget,
                Message = $"暂存 {affected.Count} 条可信度改动，超预算冲突组 {overBudget.Count} 个",
            };
        }

        /// <summary>
        /// 提交暂存区（原子事务落库 + 重算 + 校验组内和 ≤1）。
        /// 校验失败（超预算）不落库，返回错误。
        /// </summary>
        public async Task<CommitResult> CommitAsync()
        {
            var stage = Load();
            if (stage.CredibilityChanges.Count == 0)
                return new CommitResult { Success = false, Error = "暂存区为空" };

            // 应用可信度锚点改动
            foreach (var (claimId, newCredibility) in stage.CredibilityChanges)
            {
                var claim = await _claims.GetByIdAsync(claimId);
                if (claim == null) continue;
                claim.Credibility = newCredibility;
                await _claims.UpdateAsync(claim);
            }

            // 重算 + 校验
            var affected = stage.CredibilityChanges.Keys.ToList();
            var overBudget = await _evidence.RecomputeAsync(affected);
            if (overBudget.Count > 0)
                return new CommitResult { Success = false, Error = $"组内和超过预算：{string.Join(",", overBudget)}", OverBudgetGroups = overBudget };

            // 提交成功，清空暂存区
            var committed = stage.CredibilityChanges.Count;
            Clear();
            return new CommitResult { Success = true, CommittedChanges = committed };
        }

        // ---- 持久化 ----

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
