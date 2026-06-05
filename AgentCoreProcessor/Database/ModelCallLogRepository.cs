using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace AgentCoreProcessor.Database
{
    internal class ModelCallLogRepository
    {
        private readonly DbManager db;

        public ModelCallLogRepository(DbManager db)
        {
            this.db = db;
        }

        public Task<int> InsertAsync(ModelCallLog entry) => db.InsertAsync(entry);

        public Task<List<ModelCallLog>> GetRecentAsync(int count = 50, bool includeErrors = true)
            => includeErrors
                ? db.QueryAsync<ModelCallLog>(
                    "SELECT * FROM ModelCallLogs ORDER BY Timestamp DESC LIMIT ?", count)
                : db.QueryAsync<ModelCallLog>(
                    "SELECT * FROM ModelCallLogs WHERE IsError = 0 ORDER BY Timestamp DESC LIMIT ?", count);

        public Task<List<ModelCallLog>> GetSinceAsync(DateTime since)
            => db.QueryAsync<ModelCallLog>(
                "SELECT * FROM ModelCallLogs WHERE Timestamp >= ? ORDER BY Timestamp DESC", since);

        public Task<List<CoreTokenSummary>> GetByCoreAsync(DateTime? since = null)
        {
            var sql = since.HasValue
                ? "SELECT CoreName, COUNT(*) as CallCount, SUM(InputTokens) as TotalInput, SUM(OutputTokens) as TotalOutput, SUM(CacheReadTokens) as TotalCacheRead, SUM(CacheCreationTokens) as TotalCacheCreation, SUM(CacheHitTokens) as TotalCacheHit FROM ModelCallLogs WHERE IsError = 0 AND Timestamp >= ? GROUP BY CoreName ORDER BY TotalInput DESC"
                : "SELECT CoreName, COUNT(*) as CallCount, SUM(InputTokens) as TotalInput, SUM(OutputTokens) as TotalOutput, SUM(CacheReadTokens) as TotalCacheRead, SUM(CacheCreationTokens) as TotalCacheCreation, SUM(CacheHitTokens) as TotalCacheHit FROM ModelCallLogs WHERE IsError = 0 GROUP BY CoreName ORDER BY TotalInput DESC";
            return since.HasValue
                ? db.QueryAsync<CoreTokenSummary>(sql, since.Value)
                : db.QueryAsync<CoreTokenSummary>(sql);
        }

        public Task DeleteByFileNamesAsync(List<string> fileNames)
        {
            if (fileNames.Count == 0) return Task.CompletedTask;
            var placeholders = string.Join(",", fileNames.Select(_ => "?"));
            return db.ExecuteAsync(
                $"DELETE FROM ModelCallLogs WHERE LogFileName IN ({placeholders})",
                fileNames.Cast<object>().ToArray());
        }

        public Task<List<ModelTokenSummary>> GetByModelAsync(DateTime? since = null)
        {
            var sql = since.HasValue
                ? "SELECT Model, Provider, COUNT(*) as CallCount, SUM(InputTokens) as TotalInput, SUM(OutputTokens) as TotalOutput, SUM(CacheReadTokens) as TotalCacheRead, SUM(CacheCreationTokens) as TotalCacheCreation, SUM(CacheHitTokens) as TotalCacheHit FROM ModelCallLogs WHERE IsError = 0 AND Timestamp >= ? GROUP BY Model, Provider ORDER BY TotalInput DESC"
                : "SELECT Model, Provider, COUNT(*) as CallCount, SUM(InputTokens) as TotalInput, SUM(OutputTokens) as TotalOutput, SUM(CacheReadTokens) as TotalCacheRead, SUM(CacheCreationTokens) as TotalCacheCreation, SUM(CacheHitTokens) as TotalCacheHit FROM ModelCallLogs WHERE IsError = 0 GROUP BY Model, Provider ORDER BY TotalInput DESC";
            return since.HasValue
                ? db.QueryAsync<ModelTokenSummary>(sql, since.Value)
                : db.QueryAsync<ModelTokenSummary>(sql);
        }
    }

    internal class CoreTokenSummary
    {
        public string CoreName { get; set; } = "";
        public int CallCount { get; set; }
        public long TotalInput { get; set; }
        public long TotalOutput { get; set; }
        public long TotalCacheRead { get; set; }
        public long TotalCacheCreation { get; set; }
        public long TotalCacheHit { get; set; }
    }

    internal class ModelTokenSummary
    {
        public string? Model { get; set; }
        public string? Provider { get; set; }
        public int CallCount { get; set; }
        public long TotalInput { get; set; }
        public long TotalOutput { get; set; }
        public long TotalCacheRead { get; set; }
        public long TotalCacheCreation { get; set; }
        public long TotalCacheHit { get; set; }
    }
}
