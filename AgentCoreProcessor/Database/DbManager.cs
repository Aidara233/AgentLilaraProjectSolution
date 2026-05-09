using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SQLite;

namespace AgentCoreProcessor.Database
{
    /// <summary>
    /// 统一管理 SQLite 数据库连接和表初始化。
    /// 提供对各实体表的通用 CRUD 操作。
    /// </summary>
    internal class DbManager
    {
        private readonly SQLiteAsyncConnection db;

        /// <summary>
        /// 创建数据库管理器。
        /// </summary>
        /// <param name="dbPath">数据库文件的完整路径</param>
        public DbManager(string dbPath)
        {
            db = new SQLiteAsyncConnection(dbPath);
        }

        /// <summary>
        /// 初始化数据库，自动创建所有实体对应的表（已存在则跳过）。
        /// 应在应用启动时调用一次。
        /// </summary>
        public async Task InitAsync()
        {
            await db.CreateTableAsync<Person>();
            await db.CreateTableAsync<User>();
            await db.CreateTableAsync<Channel>();
            await db.CreateTableAsync<UserMessage>();
            await db.CreateTableAsync<MemoryEntry>();
            await db.CreateTableAsync<TempMemoryEntry>();
            await db.CreateTableAsync<MemoryLink>();
            await db.CreateTableAsync<PersonaMemoryEntry>();
            await db.CreateTableAsync<ReviewHint>();
            await db.CreateTableAsync<ImageRecord>();
            await db.CreateTableAsync<DreamSession>();
            await db.CreateTableAsync<DreamFragment>();
            await db.CreateTableAsync<DreamFragmentDetail>();
            await db.CreateTableAsync<ScheduledTask>();
        }

        /// <summary>
        /// 重建记忆相关表（DROP + CREATE）。用于结构变更后清除不兼容数据。
        /// </summary>
        public async Task RebuildMemoryTablesAsync()
        {
            await db.ExecuteAsync("DROP TABLE IF EXISTS TempMemories");
            await db.ExecuteAsync("DROP TABLE IF EXISTS Memories");
            await db.ExecuteAsync("DROP TABLE IF EXISTS MemoryLinks");
            await db.CreateTableAsync<TempMemoryEntry>();
            await db.CreateTableAsync<MemoryEntry>();
            await db.CreateTableAsync<MemoryLink>();
        }

        /// <summary>插入一条记录，返回受影响的行数。</summary>
        public Task<int> InsertAsync<T>(T entity) where T : new()
            => db.InsertAsync(entity);

        /// <summary>更新一条记录（按主键匹配），返回受影响的行数。</summary>
        public Task<int> UpdateAsync<T>(T entity) where T : new()
            => db.UpdateAsync(entity);

        /// <summary>删除一条记录（按主键匹配），返回受影响的行数。</summary>
        public Task<int> DeleteAsync<T>(T entity) where T : new()
            => db.DeleteAsync(entity);

        /// <summary>按主键获取单条记录，不存在时返回 null。</summary>
        public async Task<T?> GetByIdAsync<T>(int id) where T : class, new()
            => await db.FindAsync<T>(id);

        /// <summary>获取表中所有记录。</summary>
        public Task<List<T>> GetAllAsync<T>() where T : new()
            => db.Table<T>().ToListAsync();

        /// <summary>
        /// 执行原始 SQL 查询，返回结果列表。
        /// 用于 Repository 中的复杂查询。
        /// </summary>
        public Task<List<T>> QueryAsync<T>(string sql, params object[] args) where T : new()
            => db.QueryAsync<T>(sql, args);

        /// <summary>
        /// 获取某个表的 AsyncTableQuery，用于链式条件查询。
        /// </summary>
        public AsyncTableQuery<T> Table<T>() where T : new()
            => db.Table<T>();

        /// <summary>执行原始 SQL（无返回值）。</summary>
        public Task ExecuteAsync(string sql, params object[] args)
            => db.ExecuteAsync(sql, args);
    }
}