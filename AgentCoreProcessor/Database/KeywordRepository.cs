using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace AgentCoreProcessor.Database
{
    /// <summary>
    /// 关键词库数据访问。按 Name / Role 查询，支持排他属性关键词检索（冲突检测用）。
    /// </summary>
    internal class KeywordRepository
    {
        private readonly DbManager db;

        public KeywordRepository(DbManager db) => this.db = db;

        /// <summary>按主键获取关键词，不存在时返回 null。</summary>
        public Task<Keyword?> GetByIdAsync(int id) => db.GetByIdAsync<Keyword>(id);

        /// <summary>按 Name 精确查询，不存在时返回 null。</summary>
        public async Task<Keyword?> GetByNameAsync(string name)
        {
            var results = await db.QueryAsync<Keyword>(
                "SELECT * FROM Keywords WHERE Name = ? LIMIT 1", name);
            return results.Count > 0 ? results[0] : null;
        }

        /// <summary>按 Name 查找，找不到则创建并返回；已存在则返回现有记录。</summary>
        public async Task<Keyword> GetOrCreateAsync(string name, string role,
            string? aliasesJson = null, string? category = null, bool isExclusive = false)
        {
            var existing = await GetByNameAsync(name);
            if (existing != null)
                return existing;

            return await CreateAsync(name, role, aliasesJson, category, isExclusive);
        }

        /// <summary>按角色查询（attribute / keyword）。</summary>
        public Task<List<Keyword>> GetByRoleAsync(string role)
            => db.QueryAsync<Keyword>(
                "SELECT * FROM Keywords WHERE Role = ? ORDER BY Name", role);

        /// <summary>获取所有 IsExclusive=true 的属性关键词（冲突检测用）。</summary>
        public Task<List<Keyword>> GetExclusiveAsync()
            => db.QueryAsync<Keyword>(
                "SELECT * FROM Keywords WHERE IsExclusive = 1 ORDER BY Name");

        /// <summary>获取全部关键词。</summary>
        public Task<List<Keyword>> GetAllAsync()
            => db.GetAllAsync<Keyword>();

        /// <summary>
        /// 创建关键词。Name 缺省为 ""，Role 缺省为 keyword。
        /// </summary>
        public async Task<Keyword> CreateAsync(string name, string role,
            string? aliasesJson = null, string? category = null, bool isExclusive = false)
        {
            var keyword = new Keyword
            {
                Name = name,
                AliasesJson = aliasesJson,
                Role = role,
                Category = category,
                IsExclusive = isExclusive,
                UseCount = 0,
                CreatedAt = DateTime.Now,
                LastUsedAt = null
            };
            await db.InsertAsync(keyword);
            return keyword;
        }

        /// <summary>更新关键词。</summary>
        public Task<int> UpdateAsync(Keyword keyword) => db.UpdateAsync(keyword);

        /// <summary>删除关键词。</summary>
        public Task<int> DeleteAsync(Keyword keyword) => db.DeleteAsync(keyword);

        /// <summary>使用计数 +1 并刷新 LastUsedAt。</summary>
        public Task IncrementUseCountAsync(int id)
        {
            return db.ExecuteAsync(
                "UPDATE Keywords SET UseCount = UseCount + 1, LastUsedAt = ? WHERE Id = ?",
                DateTime.Now, id);
        }
    }
}