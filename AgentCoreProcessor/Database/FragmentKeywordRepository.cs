using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace AgentCoreProcessor.Database
{
    /// <summary>
    /// 片段-关键词关联表数据访问。Claim/Journal 与 Keywords 的多对多关联，
    /// 供记忆落库时建关联、检索时按关键词回溯片段使用。
    /// </summary>
    internal class FragmentKeywordRepository
    {
        private readonly DbManager db;

        public FragmentKeywordRepository(DbManager db) => this.db = db;

        /// <summary>查某片段关联的所有关键词关联。</summary>
        public async Task<List<FragmentKeyword>> GetByOwnerAsync(FragmentOwnerType ownerType, int ownerId)
        {
            return await db.QueryAsync<FragmentKeyword>(
                "SELECT * FROM FragmentKeywords WHERE OwnerType = ? AND OwnerId = ?",
                ownerType, ownerId);
        }

        /// <summary>查某关键词被哪些片段引用。</summary>
        public async Task<List<FragmentKeyword>> GetByKeywordAsync(int keywordId)
        {
            return await db.QueryAsync<FragmentKeyword>(
                "SELECT * FROM FragmentKeywords WHERE KeywordId = ?",
                keywordId);
        }

        /// <summary>查某片段关联的关键词 Id 列表。</summary>
        public async Task<List<int>> GetKeywordIdsByOwnerAsync(FragmentOwnerType ownerType, int ownerId)
        {
            var links = await GetByOwnerAsync(ownerType, ownerId);
            return links.Select(l => l.KeywordId).ToList();
        }

        /// <summary>查引用某关键词的片段 Id 列表（OwnerType 不同时 OwnerId 可能重复，调用方自行区分）。</summary>
        public async Task<List<int>> GetOwnerIdsByKeywordAsync(int keywordId)
        {
            var links = await GetByKeywordAsync(keywordId);
            return links.Select(l => l.OwnerId).ToList();
        }

        /// <summary>
        /// 创建关联（防重复：先查再插）。
        /// 已存在则返回现有记录，否则新建并返回。
        /// </summary>
        public async Task<FragmentKeyword> CreateAsync(FragmentOwnerType ownerType, int ownerId, int keywordId)
        {
            var existing = await db.QueryAsync<FragmentKeyword>(
                "SELECT * FROM FragmentKeywords WHERE OwnerType = ? AND OwnerId = ? AND KeywordId = ? LIMIT 1",
                ownerType, ownerId, keywordId);
            if (existing.Count > 0)
                return existing[0];

            var link = new FragmentKeyword
            {
                OwnerType = ownerType,
                OwnerId = ownerId,
                KeywordId = keywordId
            };
            await db.InsertAsync(link);
            return link;
        }

        /// <summary>删除某片段的所有关联（重建时用）。</summary>
        public Task DeleteByOwnerAsync(FragmentOwnerType ownerType, int ownerId)
        {
            return db.ExecuteAsync(
                "DELETE FROM FragmentKeywords WHERE OwnerType = ? AND OwnerId = ?",
                ownerType, ownerId);
        }

        /// <summary>删除一条关联。</summary>
        public Task<int> DeleteAsync(FragmentKeyword link) => db.DeleteAsync(link);
    }
}
