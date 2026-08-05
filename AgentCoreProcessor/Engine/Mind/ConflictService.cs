using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AgentCoreProcessor.Database;

namespace AgentCoreProcessor.Engine.Mind
{
    /// <summary>
    /// 冲突检测服务（《memory-redesign/05-credibility-conflict-model.md》第五节）。
    /// 排他属性冲突语义：
    /// - 排他属性 = Keywords 里 IsExclusive=true 的属性关键词（作谓词）
    /// - 触发：写入新断言，与已有断言同（subject + 排他属性谓词）槽位取值不同 → 两个就建组，后来的加入现有组
    /// - 组内成员 c_actual 和 ≤1 恒定；超预算（>1）→ 返回超预算组 Id，由上层入队 Review
    /// 由 Writer / MindEngine 持有使用。
    /// </summary>
    internal class ConflictService
    {
        private readonly ClaimRepository _claims;
        private readonly ConflictGroupRepository _groups;
        private readonly KeywordRepository _keywords;
        private readonly EvidenceEngine _evidence;

        public ConflictService(
            ClaimRepository claims,
            ConflictGroupRepository groups,
            KeywordRepository keywords,
            EvidenceEngine evidence)
        {
            _claims = claims;
            _groups = groups;
            _keywords = keywords;
            _evidence = evidence;
        }

        /// <summary>
        /// 新断言落库后检查冲突。
        /// 非排他属性 / 无同槽位取值不同断言 → 返回空列表；
        /// 有冲突 → 建组/入组，返回受影响断言 Id（新断言 + 冲突方），供上层 EvidenceEngine 重算。
        /// </summary>
        public async Task<List<int>> CheckNewClaimAsync(Claim claim)
        {
            // 1. 解析谓词表面词，判定是否为排他属性关键词
            var predicate = ClaimSlot.FromJson(claim.PredicateJson);
            var predicateIndex = predicate?.Index;
            if (string.IsNullOrWhiteSpace(predicateIndex))
                return new List<int>();

            var exclusiveKeywords = await _keywords.GetExclusiveAsync();
            bool isExclusive = exclusiveKeywords.Any(k => k.Name == predicateIndex);
            if (!isExclusive)
                return new List<int>();

            // 2. 解析主体表面词与客体表面词（entity 只存 index，不归并）
            var subject = ClaimSlot.FromJson(claim.SubjectJson);
            var subjectIndex = subject?.Index;
            if (string.IsNullOrWhiteSpace(subjectIndex))
                return new List<int>();

            var objectIndex = ClaimSlot.FromJson(claim.ObjectJson)?.Index;

            // 3. 查同 subject + 同谓词、ObjectIndex 不同的其他断言
            var sameSubject = await _claims.GetBySubjectIndexAsync(subjectIndex);
            var conflicts = new List<Claim>();
            foreach (var other in sameSubject)
            {
                if (other.Id == claim.Id) continue; // 排除新断言自身

                var otherPredicate = ClaimSlot.FromJson(other.PredicateJson);
                if (otherPredicate?.Index != predicateIndex) continue; // 谓词不同

                var otherObject = ClaimSlot.FromJson(other.ObjectJson);
                if (otherObject?.Index == objectIndex) continue; // 槽位取值相同，不算冲突

                conflicts.Add(other);
            }

            if (conflicts.Count == 0)
                return new List<int>();

            // 4. 建组/入组：按冲突槽位（subject + 排他属性）找现有组；
            //    不存在或已解决（ResolvedAt 非空）则另建新组——仓库按 Id 降序取最新一个，已解决组不应复用
            var group = await _groups.GetBySlotAsync(subjectIndex, predicateIndex);
            if (group == null || group.ResolvedAt != null)
            {
                group = await _groups.CreateAsync(new ConflictGroup
                {
                    SubjectKey = subjectIndex,
                    PredicateKey = predicateIndex,
                    Reason = $"排他属性冲突：{subjectIndex} 的「{predicateIndex}」存在不同取值",
                });
            }

            // 新断言 + 全部冲突方入组（已在组内则跳过，避免重复成员）
            await AddMemberIfAbsentAsync(group.Id, claim.Id);
            foreach (var c in conflicts)
                await AddMemberIfAbsentAsync(group.Id, c.Id);

            // 5. 返回受影响断言 Id（含新断言 + 冲突方），供上层 EvidenceEngine 重算
            var affected = new List<int> { claim.Id };
            affected.AddRange(conflicts.Select(c => c.Id));
            return affected;
        }

        /// <summary>
        /// 重算受影响断言的 c_actual，返回超预算（组内和 &gt;1）的冲突组 Id。
        /// 委托给 EvidenceEngine。
        /// </summary>
        public Task<List<int>> RecomputeGroupBudgetAsync(IReadOnlyCollection<int> affectedClaimIds)
            => _evidence.RecomputeAsync(affectedClaimIds);

        /// <summary>活跃冲突组列表（未解决）。</summary>
        public Task<List<ConflictGroup>> GetActiveGroupsAsync()
            => _groups.GetActiveAsync();

        /// <summary>成员入组（组内已存在则跳过）。</summary>
        private async Task AddMemberIfAbsentAsync(int groupId, int claimId)
        {
            var existing = await _groups.GetMemberByClaimAsync(groupId, claimId);
            if (existing != null) return;
            await _groups.AddMemberAsync(new ConflictGroupMember
            {
                GroupId = groupId,
                ClaimId = claimId,
            });
        }
    }
}
