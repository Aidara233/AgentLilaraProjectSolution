using Microsoft.Data.Sqlite;

namespace AgentCoreProcessor.Logging;

public class LogQuery : ILogQuery
{
    private readonly LogDatabase _db;

    public LogQuery(LogDatabase db)
    {
        _db = db;
    }

    public List<LogEvent> GetBySignal(string signalId)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, signal_id, scope, branch, parent_id, span_id, group_name, level, type, timestamp, name, detail
            FROM events
            WHERE signal_id = @sig
            ORDER BY timestamp
            """;
        cmd.Parameters.AddWithValue("@sig", signalId);
        return ReadEvents(cmd);
    }

    public List<LogEvent> GetByScope(string scope, long? since = null, int limit = 200)
    {
        using var cmd = _db.Connection.CreateCommand();
        if (since.HasValue)
        {
            cmd.CommandText = """
                SELECT id, signal_id, scope, branch, parent_id, span_id, group_name, level, type, timestamp, name, detail
                FROM events
                WHERE scope = @scope AND timestamp >= @since
                ORDER BY timestamp DESC
                LIMIT @limit
                """;
            cmd.Parameters.AddWithValue("@since", since.Value);
        }
        else
        {
            cmd.CommandText = """
                SELECT id, signal_id, scope, branch, parent_id, span_id, group_name, level, type, timestamp, name, detail
                FROM events
                WHERE scope = @scope
                ORDER BY timestamp DESC
                LIMIT @limit
                """;
        }
        cmd.Parameters.AddWithValue("@scope", scope);
        cmd.Parameters.AddWithValue("@limit", limit);
        return ReadEvents(cmd);
    }

    public List<LogEvent> GetRecent(int limit = 200, string? group = null, int? minLevel = null)
    {
        using var cmd = _db.Connection.CreateCommand();
        var conditions = new List<string>();
        if (group != null)
        {
            conditions.Add("group_name = @group");
            cmd.Parameters.AddWithValue("@group", group);
        }
        if (minLevel.HasValue)
        {
            conditions.Add("level >= @minLevel");
            cmd.Parameters.AddWithValue("@minLevel", minLevel.Value);
        }

        var where = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : "";
        cmd.CommandText = $"""
            SELECT id, signal_id, scope, branch, parent_id, span_id, group_name, level, type, timestamp, name, detail
            FROM events
            {where}
            ORDER BY timestamp DESC
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@limit", limit);
        return ReadEvents(cmd);
    }

    public List<LogEvent> GetOpenSpans()
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
            SELECT e1.id, e1.signal_id, e1.scope, e1.branch, e1.parent_id, e1.span_id, e1.group_name, e1.level, e1.type, e1.timestamp, e1.name, e1.detail
            FROM events e1
            WHERE e1.type = 'open'
              AND NOT EXISTS (
                  SELECT 1 FROM events e2
                  WHERE e2.span_id = e1.span_id AND e2.type = 'close'
              )
            ORDER BY e1.timestamp
            """;
        return ReadEvents(cmd);
    }

    public List<LogEvent> GetSignalList(int limit = 50)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, signal_id, scope, branch, parent_id, span_id, group_name, level, type, timestamp, name, detail
            FROM events
            WHERE type = 'open' AND parent_id IS NULL
            ORDER BY timestamp DESC
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@limit", limit);
        return ReadEvents(cmd);
    }

    public List<TokenUsageRecord> GetTokenUsage(long? since = null, string? model = null, string? callerTag = null)
    {
        using var cmd = _db.Connection.CreateCommand();
        var conditions = new List<string>();
        if (since.HasValue)
        {
            conditions.Add("timestamp >= @since");
            cmd.Parameters.AddWithValue("@since", since.Value);
        }
        if (model != null)
        {
            conditions.Add("model = @model");
            cmd.Parameters.AddWithValue("@model", model);
        }
        if (callerTag != null)
        {
            conditions.Add("caller_tag = @callerTag");
            cmd.Parameters.AddWithValue("@callerTag", callerTag);
        }

        var where = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : "";
        cmd.CommandText = $"""
            SELECT timestamp, model, caller_tag, tokens_in, tokens_out, cached_in, elapsed_ms, success
            FROM token_usage
            {where}
            ORDER BY timestamp DESC
            """;
        return ReadTokenUsage(cmd);
    }

    private static List<LogEvent> ReadEvents(SqliteCommand cmd)
    {
        var results = new List<LogEvent>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new LogEvent
            {
                Id = reader.GetInt64(0),
                SignalId = reader.GetString(1),
                Scope = reader.GetString(2),
                Branch = reader.GetInt64(3),
                ParentId = reader.IsDBNull(4) ? null : reader.GetString(4),
                SpanId = reader.IsDBNull(5) ? null : reader.GetString(5),
                GroupName = reader.GetString(6),
                Level = reader.GetInt32(7),
                Type = reader.GetString(8),
                Timestamp = reader.GetInt64(9),
                Name = reader.GetString(10),
                Detail = reader.IsDBNull(11) ? null : reader.GetString(11)
            });
        }
        return results;
    }

    private static List<TokenUsageRecord> ReadTokenUsage(SqliteCommand cmd)
    {
        var results = new List<TokenUsageRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new TokenUsageRecord
            {
                Timestamp = reader.GetInt64(0),
                Model = reader.GetString(1),
                CallerTag = reader.IsDBNull(2) ? null : reader.GetString(2),
                TokensIn = reader.GetInt32(3),
                TokensOut = reader.GetInt32(4),
                CachedIn = reader.GetInt32(5),
                ElapsedMs = reader.IsDBNull(6) ? 0 : reader.GetInt32(6),
                Success = reader.GetInt32(7) != 0
            });
        }
        return results;
    }
}
