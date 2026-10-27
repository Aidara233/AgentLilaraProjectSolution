using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace AgentCoreProcessor.Database
{
    /// <summary>
    /// 冲突组数据访问。管理冲突组本体及其成员关系，
    /// 提供冲突槽位查询、活跃组查询、成员/断言归属查询等操作。
    /// </summary>
    internal class ConflictGroupRepository
    {
        private readonly DbManager db;

        public ConflictGroupRepository(DbManager db) => this.db = db;

        // ---- 组方法 ----

        /// <summary>创建冲突组，返回带 Id 的实体。</summary>
        public async Task<ConflictGroup> CreateAsync(ConflictGroup group)
        {
            await db.InsertAsync(group);
            return group;
        }

        /// <summary>按主键获取冲突组，不存在时返回 null。</summary>
        public Task<ConflictGroup?> GetByIdAsync(int id)
            => db.GetByIdAsync<ConflictGroup>(id);

        /// <summary>
        /// 查某冲突槽位的组。按 Id 降序取最新一个（若已解决，新冲突应另建组），
        /// 不存在时返回 null。
        /// </summary>
        public async Task<ConflictGroup?> GetBySlotAsync(string subjectKey, string predicateKey)
        {
            var results = await db.QueryAsync<ConflictGroup>(
                "SELECT * FROM ConflictGroups WHERE SubjectKey = ? AND PredicateKey = ? ORDER BY Id DESC LIMIT 1",
                subjectKey, predicateKey);
            return results.Count > 0 ? results[0] : null;
        }

        /// <summary>获取所有未解决（ResolvedAt 为空）的冲突组。</summary>
        public Task<List<ConflictGroup>> GetActiveAsync()
        {
            return db.QueryAsync<ConflictGroup>(
                "SELECT * FROM ConflictGroups WHERE ResolvedAt IS NULL");
        }

        /// <summary>获取全部冲突组。</summary>
        public Task<List<ConflictGroup>> GetAllAsync()
            => db.GetAllAsync<ConflictGroup>();

        /// <summary>更新冲突组（如标 ResolvedAt/ResolvedBy），返回受影响行数。</summary>
        public Task<int> UpdateAsync(ConflictGroup group)
            => db.UpdateAsync(group);

        // ---- 成员方法 ----

        /// <summary>向组内添加一个成员（加入时间戳由实体默认值生成）。</summary>
        public async Task<ConflictGroupMember> AddMemberAsync(ConflictGroupMember member)
        {
            await db.InsertAsync(member);
            return member;
        }

        /// <summary>获取某组的所有成员。</summary>
        public Task<List<ConflictGroupMember>> GetMembersAsync(int groupId)
        {
            return db.Table<ConflictGroupMember>()
                .Where(m => m.GroupId == groupId)
                .ToListAsync();
        }

        /// <summary>获取某组的成员 ClaimId 列表。</summary>
        public async Task<List<int>> GetMemberClaimIdsAsync(int groupId)
        {
            var members = await GetMembersAsync(groupId);
            return members.Select(m => m.ClaimId).ToList();
        }

        /// <summary>查某断言（claimId）属于哪些冲突组。</summary>
        public Task<List<ConflictGroup>> GetGroupsForClaimAsync(int claimId)
        {
            return db.QueryAsync<ConflictGroup>(
                "SELECT g.* FROM ConflictGroups g " +
                "INNER JOIN ConflictGroupMembers m ON m.GroupId = g.Id " +
                "WHERE m.ClaimId = ?",
                claimId);
        }

        /// <summary>移除组内一个成员，返回受影响行数。</summary>
        public Task<int> RemoveMemberAsync(ConflictGroupMember member)
            => db.DeleteAsync(member);

        /// <summary>查某组里某断言是否已在（存在则返回成员，否则返回 null）。</summary>
        public async Task<ConflictGroupMember?> GetMemberByClaimAsync(int groupId, int claimId)
        {
            var results = await db.QueryAsync<ConflictGroupMember>(
                "SELECT * FROM ConflictGroupMembers WHERE GroupId = ? AND ClaimId = ? LIMIT 1",
                groupId, claimId);
            return results.Count > 0 ? results[0] : null;
        }
    }
}
