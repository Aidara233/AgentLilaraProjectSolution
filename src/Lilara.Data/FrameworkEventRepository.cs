using System.Collections.Generic;
using System.Threading.Tasks;

namespace AgentCoreProcessor.Database
{
    /// <summary>
    /// 框架事件数据访问。按层级/类型/时间倒序查询框架事件。
    /// </summary>
    internal class FrameworkEventRepository
    {
        private readonly DbManager db;

        public FrameworkEventRepository(DbManager db) => this.db = db;

        /// <summary>创建一条框架事件。</summary>
        public Task<int> CreateAsync(FrameworkEvent evt)
            => db.InsertAsync(evt);

        /// <summary>按 ID 查询单条框架事件，不存在时返回 null。</summary>
        public Task<FrameworkEvent?> GetByIdAsync(int id)
            => db.GetByIdAsync<FrameworkEvent>(id);

        /// <summary>按事件类型查询最近 N 条（时间倒序）。</summary>
        public Task<List<FrameworkEvent>> GetByTypeAsync(string type, int limit)
            => db.QueryAsync<FrameworkEvent>(
                "SELECT * FROM FrameworkEvents WHERE Type = ? ORDER BY Timestamp DESC LIMIT ?", type, limit);

        /// <summary>按记录层级（L0/L1）查询最近 N 条（时间倒序）。</summary>
        public Task<List<FrameworkEvent>> GetByLevelAsync(string level, int limit)
            => db.QueryAsync<FrameworkEvent>(
                "SELECT * FROM FrameworkEvents WHERE Level = ? ORDER BY Timestamp DESC LIMIT ?", level, limit);

        /// <summary>按时间倒序获取最近 N 条框架事件。</summary>
        public Task<List<FrameworkEvent>> GetRecentAsync(int limit)
            => db.QueryAsync<FrameworkEvent>(
                "SELECT * FROM FrameworkEvents ORDER BY Timestamp DESC LIMIT ?", limit);

        /// <summary>获取全部框架事件。</summary>
        public Task<List<FrameworkEvent>> GetAllAsync()
            => db.GetAllAsync<FrameworkEvent>();
    }
}
