using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AgentCoreProcessor.Database;

namespace AgentCoreProcessor.WebUI.Services
{
    internal class TokenStatsService
    {
        private readonly ModelCallLogRepository repo;

        public TokenStatsService(ModelCallLogRepository repo)
        {
            this.repo = repo;
        }

        public Task<List<CoreTokenSummary>> GetByCoreAsync(DateTime? since = null)
            => repo.GetByCoreAsync(since);

        public Task<List<ModelTokenSummary>> GetByModelAsync(DateTime? since = null)
            => repo.GetByModelAsync(since);

        public Task<List<ModelCallLog>> GetRecentAsync(int count = 50)
            => repo.GetRecentAsync(count);

        public async Task<CacheStats> GetCacheStatsAsync(DateTime? since = null)
        {
            var logs = since.HasValue
                ? await repo.GetSinceAsync(since.Value)
                : await repo.GetRecentAsync(1000);

            long totalInput = 0, totalCacheRead = 0, totalCacheCreation = 0, totalCacheHit = 0;
            foreach (var log in logs)
            {
                totalInput += log.InputTokens;
                totalCacheRead += log.CacheReadTokens;
                totalCacheCreation += log.CacheCreationTokens;
                totalCacheHit += log.CacheHitTokens;
            }

            var totalCacheable = totalInput + totalCacheRead + totalCacheCreation;
            return new CacheStats
            {
                TotalInputTokens = totalInput,
                TotalCacheRead = totalCacheRead,
                TotalCacheCreation = totalCacheCreation,
                TotalCacheHit = totalCacheHit,
                HitRate = totalCacheable > 0
                    ? (double)(totalCacheRead + totalCacheHit) / totalCacheable * 100
                    : 0
            };
        }
    }

    internal class CacheStats
    {
        public long TotalInputTokens { get; set; }
        public long TotalCacheRead { get; set; }
        public long TotalCacheCreation { get; set; }
        public long TotalCacheHit { get; set; }
        public double HitRate { get; set; }
    }
}
