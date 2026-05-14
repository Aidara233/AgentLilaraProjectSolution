using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace AgentCoreProcessor.Logging;

public class TokenAggregator
{
    private readonly LogDatabase _db;

    public TokenAggregator(LogDatabase db) => _db = db;

    public void ProcessBatch(List<LogEvent> batch, SqliteTransaction? tx = null)
    {
        foreach (var evt in batch)
        {
            if (evt.Type != "close" || evt.GroupName != LogGroup.Model || evt.Detail == null)
                continue;

            try
            {
                using var doc = JsonDocument.Parse(evt.Detail);
                var root = doc.RootElement;

                using var cmd = _db.Connection.CreateCommand();
                if (tx != null) cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO token_usage (timestamp, model, caller_tag, tokens_in, tokens_out, cached_in, elapsed_ms, success)
                    VALUES (@ts, @model, @caller, @tin, @tout, @cached, @elapsed, @success)
                    """;
                cmd.Parameters.AddWithValue("@ts", evt.Timestamp);
                cmd.Parameters.AddWithValue("@model", root.TryGetProperty("model", out var m) ? m.GetString() ?? "" : "");
                cmd.Parameters.AddWithValue("@caller", root.TryGetProperty("caller_tag", out var c) ? (object)(c.GetString() ?? "") : DBNull.Value);
                cmd.Parameters.AddWithValue("@tin", root.TryGetProperty("tokens_in", out var ti) ? ti.GetInt32() : 0);
                cmd.Parameters.AddWithValue("@tout", root.TryGetProperty("tokens_out", out var to) ? to.GetInt32() : 0);
                cmd.Parameters.AddWithValue("@cached", root.TryGetProperty("cached_in", out var ci) ? ci.GetInt32() : 0);
                cmd.Parameters.AddWithValue("@elapsed", root.TryGetProperty("elapsed_ms", out var el) ? el.GetInt32() : (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@success", root.TryGetProperty("error", out var err) && err.ValueKind != JsonValueKind.Null ? 0 : 1);
                cmd.ExecuteNonQuery();
            }
            catch { }
        }
    }
}
