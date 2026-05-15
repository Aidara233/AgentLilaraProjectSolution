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

        using var cmd2 = _conn.CreateCommand();
        cmd2.CommandText = """
            CREATE TABLE IF NOT EXISTS events (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                signal_id   TEXT NOT NULL,
                scope       TEXT NOT NULL,
                branch      INTEGER NOT NULL DEFAULT 0,
                parent_id   TEXT,
                span_id     TEXT,
                group_name  TEXT NOT NULL,
                level       INTEGER NOT NULL DEFAULT 1,
                type        TEXT NOT NULL DEFAULT 'event',
                timestamp   INTEGER NOT NULL,
                name        TEXT NOT NULL,
                detail      TEXT
            );

            CREATE INDEX IF NOT EXISTS idx_events_signal ON events(signal_id);
            CREATE INDEX IF NOT EXISTS idx_events_scope_time ON events(scope, timestamp);
            CREATE INDEX IF NOT EXISTS idx_events_branch ON events(branch);
            CREATE INDEX IF NOT EXISTS idx_events_span ON events(span_id);
            CREATE INDEX IF NOT EXISTS idx_events_group_time ON events(group_name, timestamp);
            CREATE INDEX IF NOT EXISTS idx_events_level_time ON events(level, timestamp);
            CREATE INDEX IF NOT EXISTS idx_events_parent ON events(parent_id);

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
        cmd2.ExecuteNonQuery();
    }

    private void MigrateParentIdToText()
    {
        using var check = _conn.CreateCommand();
        check.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='events'";
        var exists = (long)check.ExecuteScalar()! > 0;
        if (!exists) return;

        using var typeCheck = _conn.CreateCommand();
        typeCheck.CommandText = "PRAGMA table_info(events)";
        using var reader = typeCheck.ExecuteReader();
        while (reader.Read())
        {
            if (reader.GetString(1) == "parent_id")
            {
                var colType = reader.GetString(2).ToUpperInvariant();
                if (colType == "INTEGER" || colType == "")
                {
                    using var drop = _conn.CreateCommand();
                    drop.CommandText = "DROP TABLE events";
                    drop.ExecuteNonQuery();
                }
                break;
            }
        }
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
