using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace AgentCoreProcessor.Database
{
    /// <summary>
    /// 断言数据访问。按槽位/同源/关键词查询，供 Writer/ConflictService/RetrieveService 使用。
    /// </summary>
    internal class ClaimRepository
    {
        private readonly DbManager db;

        public ClaimRepository(DbManager db) => this.db = db;

        public async Task<Claim> CreateAsync(Claim claim)
        {
            claim.RecordedAt = DateTime.Now;
            await db.InsertAsync(claim);
            return claim;
        }

        public Task<Claim?> GetByIdAsync(int id) => db.GetByIdAsync<Claim>(id);

        public Task<List<Claim>> GetAllAsync() => db.GetAllAsync<Claim>();

        public Task<int> UpdateAsync(Claim claim) => db.UpdateAsync(claim);

        public Task<int> DeleteAsync(Claim claim) => db.DeleteAsync(claim);

        /// <summary>按同源分组查询（一句话拆多条断言的共同出身，防重复计分）。</summary>
        public Task<List<Claim>> GetByUtteranceAsync(string utteranceId)
        {
            return db.Table<Claim>().Where(c => c.UtteranceId == utteranceId).ToListAsync();
        }

        /// <summary>按主体表面词查询（冲突检测用）。</summary>
        public Task<List<Claim>> GetBySubjectIndexAsync(string subjectIndex)
        {
            return db.QueryAsync<Claim>(
                "SELECT * FROM Claims WHERE SubjectJson LIKE ?", $"%\"{subjectIndex}\"%");
        }

        /// <summary>按谓词表面词查询。</summary>
        public Task<List<Claim>> GetByPredicateIndexAsync(string predicateIndex)
        {
            return db.QueryAsync<Claim>(
                "SELECT * FROM Claims WHERE PredicateJson LIKE ?", $"%\"{predicateIndex}\"%");
        }

        /// <summary>按客体表面词查询。</summary>
        public Task<List<Claim>> GetByObjectIndexAsync(string objectIndex)
        {
            return db.QueryAsync<Claim>(
                "SELECT * FROM Claims WHERE ObjectJson LIKE ?", $"%\"{objectIndex}\"%");
        }

        /// <summary>按关联关键词查询（经 FragmentKeywords JOIN）。</summary>
        public Task<List<Claim>> GetByKeywordIdAsync(int keywordId)
        {
            return db.QueryAsync<Claim>(
                "SELECT c.* FROM Claims c JOIN FragmentKeywords fk ON fk.OwnerId = c.Id " +
                "WHERE fk.OwnerType = 0 AND fk.KeywordId = ?", keywordId);
        }

        /// <summary>按原始文本 LIKE 搜索。</summary>
        public Task<List<Claim>> SearchRawAsync(string keyword, int limit = 20)
        {
            return db.QueryAsync<Claim>(
                "SELECT * FROM Claims WHERE Raw LIKE ? ORDER BY RecordedAt DESC LIMIT ?",
                $"%{keyword}%", limit);
        }

        /// <summary>统计总数。</summary>
        public Task<int> CountAsync() => db.Table<Claim>().CountAsync();
    }
}
