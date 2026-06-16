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

        /// <summary>
        /// 绕过 sqlite-net-pcl 1.9.172 bug：CreateTable 创建新表后未重新查询 PRAGMA table_info，
        /// 导致迁移代码试图 ALTER TABLE ADD COLUMN（含 PRIMARY KEY），SQLite 拒绝。
        /// </summary>
        private async Task SafeCreateTable(string sql)
        {
            try { await db.ExecuteAsync(sql); }
            catch (Exception ex) { Console.Error.WriteLine($"[DB] SafeCreateTable FAILED: {ex.Message}"); throw; }
        }

        private const string T_PERSON = @"
            CREATE TABLE IF NOT EXISTS Persons (
                Id INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL,
                Name TEXT NOT NULL,
                Aliases TEXT,
                TrustLevel INTEGER,
                TrustProgress REAL,
                FastMemory TEXT,
                AlertLevel INTEGER,
                LastAlertTime INTEGER,
                CreatedAt INTEGER
            )";

        private const string T_USER = @"
            CREATE TABLE IF NOT EXISTS Users (
                Id INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL,
                PersonId INTEGER,
                Platform TEXT,
                PlatformId TEXT,
                PermissionLevel INTEGER,
                DisplayName TEXT
            )";

        private const string T_CHANNEL = @"
            CREATE TABLE IF NOT EXISTS Channels (
                Id INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL,
                Name TEXT NOT NULL,
                Affinity REAL,
                LastExtractedMessageId INTEGER
            )";

        private const string T_USERMSG = @"
            CREATE TABLE IF NOT EXISTS UserMessages (
                Id INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL,
                UserId INTEGER,
                ChannelId INTEGER,
                Content TEXT,
                SenderName TEXT,
                Time INTEGER,
                IsFromBot INTEGER,
                PlatformMessageId TEXT,
                ImageCount INTEGER,
                ImageHashes TEXT,
                ReplyToPlatformMessageId TEXT,
                MentionedPlatformIds TEXT
            )";

        private const string T_MEMORY = @"
            CREATE TABLE IF NOT EXISTS Memories (
                Id INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL,
                PersonId INTEGER,
                ChannelId INTEGER,
                Type TEXT,
                Subject TEXT,
                Content TEXT,
                Embedding BLOB,
                Importance REAL,
                Confidence TEXT,
                Feedback TEXT,
                SourceMessageId INTEGER,
                SourceMemoryIds TEXT,
                IsDerived INTEGER,
                SourceHash TEXT,
                LastDreamTime INTEGER,
                IsPersistent INTEGER,
                ExpiresAt INTEGER,
                CreatedAt INTEGER,
                LastAccessedAt INTEGER,
                Certainty REAL,
                RecallCount INTEGER,
                LastRecalledAt INTEGER,
                IsSuperseded INTEGER,
                EmbeddingModel TEXT
            )";

        private const string T_TEMPMEM = @"
            CREATE TABLE IF NOT EXISTS TempMemories (
                Id INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL,
                PersonId INTEGER,
                ChannelId INTEGER,
                Type TEXT,
                Subject TEXT,
                Content TEXT,
                Embedding BLOB,
                SourceMessageId INTEGER,
                Confidence TEXT,
                CreatedAt INTEGER,
                Heat REAL,
                EmbeddingModel TEXT
            )";

        private const string T_MEMLINK = @"
            CREATE TABLE IF NOT EXISTS MemoryLinks (
                Id INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL,
                SourceId INTEGER,
                TargetId INTEGER,
                Strength REAL,
                LinkType TEXT,
                CreatedAt INTEGER,
                UpdatedAt INTEGER,
                Support REAL
            )";

        private const string T_PERSONAMEM = @"
            CREATE TABLE IF NOT EXISTS PersonaMemories (
                Id INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL,
                Content TEXT,
                Embedding BLOB,
                Category TEXT,
                CreatedAt INTEGER,
                EmbeddingModel TEXT
            )";

        private const string T_BEACON = @"
            CREATE TABLE IF NOT EXISTS Beacons (
                Id INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL,
                Content TEXT,
                MessageId INTEGER,
                PersonId INTEGER,
                ChannelId INTEGER,
                Source TEXT,
                Consumer TEXT,
                IsProcessed INTEGER,
                CreatedAt INTEGER,
                ProcessedAt INTEGER
            )";

        private const string T_IMAGE = @"
            CREATE TABLE IF NOT EXISTS ImageRecords (
                Id INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL,
                Hash TEXT,
                LocalPath TEXT,
                ThumbnailPath TEXT,
                SourceUrl TEXT,
                Category TEXT,
                Description TEXT,
                OcrText TEXT,
                HasText INTEGER,
                SeenCount INTEGER,
                FileSize INTEGER,
                CreatedAt INTEGER,
                Phase INTEGER,
                Classification TEXT,
                FirstSeenMessageId INTEGER,
                RefineFocus TEXT
            )";

        private const string T_DREAMSESSION = @"
            CREATE TABLE IF NOT EXISTS DreamSessions (
                Id INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL,
                Level TEXT,
                StartTime INTEGER,
                EndTime INTEGER,
                FragmentsExecuted INTEGER,
                WasInterrupted INTEGER
            )";

        private const string T_DREAMFRAG = @"
            CREATE TABLE IF NOT EXISTS DreamFragments (
                Id INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL,
                SessionId INTEGER,
                Type TEXT,
                SeqIndex INTEGER,
                StartTime INTEGER,
                DurationSeconds REAL,
                Success INTEGER,
                Summary TEXT,
                InputMemoryIds TEXT,
                OutputRaw TEXT
            )";

        private const string T_DREAMFRAGDETAIL = @"
            CREATE TABLE IF NOT EXISTS DreamFragmentDetails (
                Id INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL,
                FragmentId INTEGER,
                Action TEXT,
                MemoryId INTEGER,
                OldValue TEXT,
                NewValue TEXT,
                Note TEXT
            )";

        private const string T_MODELCALLLOG = @"
            CREATE TABLE IF NOT EXISTS ModelCallLogs (
                Id INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL,
                Timestamp INTEGER,
                CoreName TEXT,
                Model TEXT,
                Provider TEXT,
                InputTokens INTEGER,
                OutputTokens INTEGER,
                CacheCreationTokens INTEGER,
                CacheReadTokens INTEGER,
                CacheHitTokens INTEGER,
                LogFileName TEXT,
                IsError INTEGER
            )";

        private const string T_PERSONTRAIT = @"
            CREATE TABLE IF NOT EXISTS PersonTraits (
                Id INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL,
                PersonId INTEGER,
                Category TEXT,
                Key TEXT,
                Value TEXT,
                Confidence REAL,
                SourceHint TEXT,
                UpdatedAt INTEGER
            )";

        private const string T_EVALSCORE = @"
            CREATE TABLE IF NOT EXISTS EvaluationScores (
                Id INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL,
                TargetType TEXT,
                TargetId INTEGER,
                Dimension TEXT,
                Value REAL,
                LastEvaluatedAt INTEGER
            )";

        private const string T_REVIEWSESSION = @"
            CREATE TABLE IF NOT EXISTS ReviewSessions (
                Id INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL,
                SignalId TEXT,
                StartTime INTEGER,
                EndTime INTEGER,
                SeedType TEXT,
                StopReason TEXT,
                TokensUsed INTEGER,
                RoundsExecuted INTEGER,
                ChannelsVisited TEXT,
                PersonsEncountered TEXT,
                ThinkingNotes TEXT,
                EvaluationCount INTEGER,
                RawEvaluations TEXT
            )";

        private const string T_REVIEWACTION = @"
            CREATE TABLE IF NOT EXISTS ReviewActions (
                Id INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL,
                SessionId INTEGER,
                SeqIndex INTEGER,
                Time INTEGER,
                ActionType TEXT,
                Summary TEXT,
                Detail TEXT
            )";

        public async Task InitAsync()
        {
            await SafeCreateTable(T_PERSON);
            await SafeCreateTable(T_USER);
            await SafeCreateTable(T_CHANNEL);
            await SafeCreateTable(T_USERMSG);
            await SafeCreateTable(T_MEMORY);
            await SafeCreateTable(T_TEMPMEM);
            await SafeCreateTable(T_MEMLINK);
            await SafeCreateTable(T_PERSONAMEM);
            await SafeCreateTable(T_BEACON);
            await SafeCreateTable(T_IMAGE);
            await SafeCreateTable(T_DREAMSESSION);
            await SafeCreateTable(T_DREAMFRAG);
            await SafeCreateTable(T_DREAMFRAGDETAIL);
            await SafeCreateTable(T_MODELCALLLOG);
            await SafeCreateTable(T_PERSONTRAIT);
            await SafeCreateTable(T_EVALSCORE);
            await SafeCreateTable(T_REVIEWSESSION);
            await SafeCreateTable(T_REVIEWACTION);

            // 索引（sqlite-net [Indexed] 属性的等价物）
            await db.ExecuteAsync("CREATE UNIQUE INDEX IF NOT EXISTS idx_ImageRecords_Hash ON ImageRecords(Hash)");
            await db.ExecuteAsync("CREATE INDEX IF NOT EXISTS idx_DreamFragments_SessionId ON DreamFragments(SessionId)");
            await db.ExecuteAsync("CREATE INDEX IF NOT EXISTS idx_DreamFragmentDetails_FragmentId ON DreamFragmentDetails(FragmentId)");
            await db.ExecuteAsync("CREATE INDEX IF NOT EXISTS idx_ModelCallLogs_Timestamp ON ModelCallLogs(Timestamp)");
            await db.ExecuteAsync("CREATE INDEX IF NOT EXISTS idx_ModelCallLogs_CoreName ON ModelCallLogs(CoreName)");
            await db.ExecuteAsync("CREATE INDEX IF NOT EXISTS idx_PersonTraits_PersonId ON PersonTraits(PersonId)");
            await db.ExecuteAsync("CREATE UNIQUE INDEX IF NOT EXISTS idx_EvaluationScores_Target ON EvaluationScores(TargetType, TargetId, Dimension)");
            await db.ExecuteAsync("CREATE INDEX IF NOT EXISTS idx_ReviewActions_SessionId ON ReviewActions(SessionId)");
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
