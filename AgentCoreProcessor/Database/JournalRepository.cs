using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace AgentCoreProcessor.Database
{
    /// <summary>
    /// 经历数据访问。按频道/类型/关键词查询，供 Writer/RetrieveService/经历注入使用。
    /// </summary>
    internal class JournalRepository
    {
        private readonly DbManager db;

        public JournalRepository(DbManager db) => this.db = db;

        public async Task<Journal> CreateAsync(Journal journal)
        {
            journal.CreatedAt = DateTime.Now;
            await db.InsertAsync(journal);
            return journal;
        }

        public Task<Journal?> GetByIdAsync(int id) => db.GetByIdAsync<Journal>(id);

        public Task<List<Journal>> GetAllAsync() => db.GetAllAsync<Journal>();

        public Task<int> UpdateAsync(Journal journal) => db.UpdateAsync(journal);

        public Task<int> DeleteAsync(Journal journal) => db.DeleteAsync(journal);

        /// <summary>按频道查询（来源锚点）。</summary>
        public Task<List<Journal>> GetByChannelAsync(int channelId, int limit = 50)
        {
            return db.QueryAsync<Journal>(
                "SELECT * FROM Journals WHERE ChannelId = ? ORDER BY OccurredAt DESC LIMIT ?",
                channelId, limit);
        }

        /// <summary>按经历类型查询（Experience=L2 / Reflection=L3）。</summary>
        public Task<List<Journal>> GetByKindAsync(string kind, int limit = 50)
        {
            return db.QueryAsync<Journal>(
                "SELECT * FROM Journals WHERE Kind = ? ORDER BY OccurredAt DESC LIMIT ?",
                kind, limit);
        }

        /// <summary>最近 N 条（时间倒序）。</summary>
        public Task<List<Journal>> GetRecentAsync(int limit)
        {
            return db.QueryAsync<Journal>(
                "SELECT * FROM Journals ORDER BY OccurredAt DESC LIMIT ?", limit);
        }

        /// <summary>按关联关键词查询（经 FragmentKeywords JOIN）。</summary>
        public Task<List<Journal>> GetByKeywordIdAsync(int keywordId, int limit = 50)
        {
            return db.QueryAsync<Journal>(
                "SELECT j.* FROM Journals j JOIN FragmentKeywords fk ON fk.OwnerId = j.Id " +
                "WHERE fk.OwnerType = 1 AND fk.KeywordId = ? ORDER BY j.OccurredAt DESC LIMIT ?",
                keywordId, limit);
        }

        /// <summary>统计总数。</summary>
        public Task<int> CountAsync() => db.Table<Journal>().CountAsync();
    }
}
