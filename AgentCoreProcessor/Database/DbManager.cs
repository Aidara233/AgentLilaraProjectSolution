using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
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

        public DbManager(string dbPath)
        {
            // 用 Microsoft.Data.Sqlite 设置 WAL 模式（sqlite-net-pcl 的 Execute 对 PRAGMA 会误抛异常）
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
            using (var setupConn = new SqliteConnection($"Data Source={dbPath}"))
            {
                setupConn.Open();
                using var cmd = setupConn.CreateCommand();
                cmd.CommandText = "PRAGMA journal_mode=WAL;";
                cmd.ExecuteNonQuery();
                cmd.CommandText = "PRAGMA synchronous=NORMAL;";
                cmd.ExecuteNonQuery();
            }

            db = new SQLiteAsyncConnection(dbPath);
        }

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
            await db.CreateTableAsync<Beacon>();
            await db.CreateTableAsync<ImageRecord>();
            await db.CreateTableAsync<DreamSession>();
            await db.CreateTableAsync<DreamFragment>();
            await db.CreateTableAsync<DreamFragmentDetail>();
            await db.CreateTableAsync<ModelCallLog>();
            // migration: 2026-05-24 添加 IsError 列
            try { await db.ExecuteAsync("ALTER TABLE ModelCallLogs ADD COLUMN IsError INTEGER NOT NULL DEFAULT 0"); }
            catch (Exception ex) when (ex.Message.Contains("duplicate column")) { /* 列已存在，跳过 */ }

            // migration: 2026-06-01 双轴记忆模型 (Confidence→Certainty, 命中追踪, 被取代标记)
            try { await db.ExecuteAsync("ALTER TABLE Memories ADD COLUMN Certainty REAL NOT NULL DEFAULT 1.0"); }
            catch (Exception ex) when (ex.Message.Contains("duplicate column")) { }
            try { await db.ExecuteAsync("ALTER TABLE Memories ADD COLUMN RecallCount INTEGER NOT NULL DEFAULT 0"); }
            catch (Exception ex) when (ex.Message.Contains("duplicate column")) { }
            try { await db.ExecuteAsync("ALTER TABLE Memories ADD COLUMN LastRecalledAt TEXT"); }
            catch (Exception ex) when (ex.Message.Contains("duplicate column")) { }
            try { await db.ExecuteAsync("ALTER TABLE Memories ADD COLUMN IsSuperseded INTEGER NOT NULL DEFAULT 0"); }
            catch (Exception ex) when (ex.Message.Contains("duplicate column")) { }
            // 现有数据迁移：旧 Confidence 字符串 → Certainty 浮点
            try { await db.ExecuteAsync("UPDATE Memories SET Certainty = 1.0 WHERE Confidence = 'high'"); }
            catch { }
            try { await db.ExecuteAsync("UPDATE Memories SET Certainty = 0.3 WHERE Confidence = 'low'"); }
            catch { }

            // migration: 2026-06-01 双边轴模型 (Strength→Relevance, 加 Support)
            try { await db.ExecuteAsync("ALTER TABLE MemoryLinks ADD COLUMN Support REAL NOT NULL DEFAULT 1.0"); }
            catch (Exception ex) when (ex.Message.Contains("duplicate column")) { }

            // migration: 2026-06-02 临时记忆热度模型
            try { await db.ExecuteAsync("ALTER TABLE TempMemories ADD COLUMN Heat REAL NOT NULL DEFAULT 0.3"); }
            catch (Exception ex) when (ex.Message.Contains("duplicate column")) { }
            await db.CreateTableAsync<PersonTrait>();
            await db.CreateTableAsync<EvaluationScore>();
            await db.CreateTableAsync<ReviewSession>();
            await db.CreateTableAsync<ReviewAction>();

            // migration: 2026-06-03 添加 RawEvaluations 快照列
            try { await db.ExecuteAsync("ALTER TABLE ReviewSessions ADD COLUMN RawEvaluations TEXT"); }
            catch (Exception ex) when (ex.Message.Contains("duplicate column")) { }

            // migration: 2026-06-05 向量模型版本标记
            try { await db.ExecuteAsync("ALTER TABLE Memories ADD COLUMN EmbeddingModel TEXT"); }
            catch (Exception ex) when (ex.Message.Contains("duplicate column")) { }
            try { await db.ExecuteAsync("ALTER TABLE TempMemories ADD COLUMN EmbeddingModel TEXT"); }
            catch (Exception ex) when (ex.Message.Contains("duplicate column")) { }
            try { await db.ExecuteAsync("ALTER TABLE PersonaMemories ADD COLUMN EmbeddingModel TEXT"); }
            catch (Exception ex) when (ex.Message.Contains("duplicate column")) { }
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
