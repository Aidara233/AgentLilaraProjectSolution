using Microsoft.Data.Sqlite;

namespace AgentCoreProcessor.Logging;

public class LogDatabase : IDisposable
{
    private readonly SqliteConnection _conn;

    public LogDatabase(string storagePath)
    {
        Directory.CreateDirectory(storagePath);
        var dbPath = Path.Combine(storagePath, "logs.db");
        _conn = new SqliteConnection($"Data Source={dbPath}");
        _conn.Open();
        InitSchema();
    }

    private void InitSchema()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            """;
        cmd.ExecuteNonQuery();

        MigrateParentIdToText();
        MigrateCauseSpanId();
        MigrateIsSignalOrigin();

        using var cmd2 = _conn.CreateCommand();
        cmd2.CommandText = """
            CREATE TABLE IF NOT EXISTS events (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                signal_id   TEXT NOT NULL,
                scope       TEXT NOT NULL,
                branch      INTEGER NOT NULL DEFAULT 0,
                parent_id   TEXT,
                span_id     TEXT,
                cause_span_id TEXT,
                group_name  TEXT NOT NULL,
                level       INTEGER NOT NULL DEFAULT 1,
                type        TEXT NOT NULL DEFAULT 'event',
                timestamp   INTEGER NOT NULL,
                name        TEXT NOT NULL,
                detail      TEXT,
                is_signal_origin INTEGER NOT NULL DEFAULT 0
            );

            CREATE INDEX IF NOT EXISTS idx_events_signal ON events(signal_id);
            CREATE INDEX IF NOT EXISTS idx_events_scope_time ON events(scope, timestamp);
            CREATE INDEX IF NOT EXISTS idx_events_branch ON events(branch);
            CREATE INDEX IF NOT EXISTS idx_events_span ON events(span_id);
            CREATE INDEX IF NOT EXISTS idx_events_group_time ON events(group_name, timestamp);
            CREATE INDEX IF NOT EXISTS idx_events_level_time ON events(level, timestamp);
            CREATE INDEX IF NOT EXISTS idx_events_parent ON events(parent_id);
            CREATE INDEX IF NOT EXISTS idx_events_cause ON events(cause_span_id);
            CREATE INDEX IF NOT EXISTS idx_events_time ON events(timestamp);
            CREATE INDEX IF NOT EXISTS idx_events_origin_time ON events(is_signal_origin, timestamp);
            """;
        cmd2.ExecuteNonQuery();

        using var cmd3 = _conn.CreateCommand();
        cmd3.CommandText = """
            CREATE TABLE IF NOT EXISTS token_usage (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp   INTEGER NOT NULL,
                model       TEXT NOT NULL,
                caller_tag  TEXT,
                tokens_in   INTEGER NOT NULL,
                tokens_out  INTEGER NOT NULL,
                cached_in   INTEGER DEFAULT 0,
                elapsed_ms  INTEGER,
                success     INTEGER DEFAULT 1
            );

            CREATE INDEX IF NOT EXISTS idx_token_time ON token_usage(timestamp);
            CREATE INDEX IF NOT EXISTS idx_token_model ON token_usage(model, timestamp);
            CREATE INDEX IF NOT EXISTS idx_token_caller ON token_usage(caller_tag, timestamp);
            """;
        cmd3.ExecuteNonQuery();

        InsertCeaseIfNeeded();
    }

    private void InsertCeaseIfNeeded()
    {
        using var check = _conn.CreateCommand();
        check.CommandText = """
            SELECT COUNT(*) FROM events
            WHERE type = 'open' AND span_id IS NOT NULL
              AND span_id NOT IN (SELECT span_id FROM events WHERE type = 'close' AND span_id IS NOT NULL)
            """;
        var unclosed = (long)(check.ExecuteScalar() ?? 0);
        if (unclosed == 0) return;

        var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        using var insert = _conn.CreateCommand();
        insert.CommandText = """
            INSERT INTO events (signal_id, scope, branch, parent_id, span_id, cause_span_id, group_name, level, type, timestamp, name, detail, is_signal_origin)
            VALUES ('system', 'system:init', @ts, NULL, NULL, NULL, 'Engine', 1, 'cease', @ts, '进程中断恢复', @detail, 0)
            """;
        insert.Parameters.AddWithValue("@ts", ts);
        insert.Parameters.AddWithValue("@detail", $"{{\"unclosed\":{unclosed}}}");
        insert.ExecuteNonQuery();
    }

    private void MigrateParentIdToText()
    {
        using var check = _conn.CreateCommand();
        check.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='events'";
        var exists = (long)check.ExecuteScalar()! > 0;
        if (!exists) return;

        bool needsDrop = false;
        using (var typeCheck = _conn.CreateCommand())
        {
            typeCheck.CommandText = "PRAGMA table_info(events)";
            using var reader = typeCheck.ExecuteReader();
            while (reader.Read())
            {
                if (reader.GetString(1) == "parent_id")
                {
                    var colType = reader.GetString(2).ToUpperInvariant();
                    if (colType == "INTEGER" || colType == "")
                        needsDrop = true;
                    break;
                }
            }
        }

        if (needsDrop)
        {
            using var drop = _conn.CreateCommand();
            drop.CommandText = "DROP TABLE events";
            drop.ExecuteNonQuery();
        }
    }

    private void MigrateCauseSpanId()
    {
        // Check if column already exists
        using var check = _conn.CreateCommand();
        check.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='events'";
        if ((long)check.ExecuteScalar()! == 0) return;

        bool hasColumn = false;
        using (var colCheck = _conn.CreateCommand())
        {
            colCheck.CommandText = "PRAGMA table_info(events)";
            using var reader = colCheck.ExecuteReader();
            while (reader.Read())
            {
                if (reader.GetString(1) == "cause_span_id")
                {
                    hasColumn = true;
                    break;
                }
            }
        }

        if (!hasColumn)
        {
            using var add = _conn.CreateCommand();
            add.CommandText = "ALTER TABLE events ADD COLUMN cause_span_id TEXT";
            add.ExecuteNonQuery();
        }

        // Move cross-scope parent_id values to cause_span_id
        using var migrate = _conn.CreateCommand();
        migrate.CommandText = """
            UPDATE events SET
                cause_span_id = parent_id,
                parent_id = NULL
            WHERE parent_id IS NOT NULL
              AND cause_span_id IS NULL
              AND EXISTS (
                  SELECT 1 FROM events e2
                  WHERE e2.span_id = events.parent_id
                    AND e2.scope != events.scope
              )
            """;
        migrate.ExecuteNonQuery();
    }

    private void MigrateIsSignalOrigin()
    {
        using var check = _conn.CreateCommand();
        check.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='events'";
        if ((long)check.ExecuteScalar()! == 0) return;

        bool hasColumn = false;
        using (var colCheck = _conn.CreateCommand())
        {
            colCheck.CommandText = "PRAGMA table_info(events)";
            using var reader = colCheck.ExecuteReader();
            while (reader.Read())
            {
                if (reader.GetString(1) == "is_signal_origin")
                {
                    hasColumn = true;
                    break;
                }
            }
        }

        if (!hasColumn)
        {
            using var add = _conn.CreateCommand();
            add.CommandText = "ALTER TABLE events ADD COLUMN is_signal_origin INTEGER NOT NULL DEFAULT 0";
            add.ExecuteNonQuery();
        }

        // Backfill: only Signal.Begin events (no parent, no cause) are true origins
        using var backfill = _conn.CreateCommand();
        backfill.CommandText = """
            UPDATE events SET is_signal_origin = 1
            WHERE type = 'open' AND parent_id IS NULL AND cause_span_id IS NULL
            """;
        backfill.ExecuteNonQuery();
    }

    public SqliteConnection Connection => _conn;

    public void Cleanup(int retainDays, int tokenRetainDays)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-retainDays).ToUnixTimeMilliseconds();
        var tokenCutoff = DateTimeOffset.UtcNow.AddDays(-tokenRetainDays).ToUnixTimeMilliseconds();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM events WHERE timestamp < @cutoff; DELETE FROM token_usage WHERE timestamp < @tokenCutoff;";
        cmd.Parameters.AddWithValue("@cutoff", cutoff);
        cmd.Parameters.AddWithValue("@tokenCutoff", tokenCutoff);
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _conn?.Dispose();
}
