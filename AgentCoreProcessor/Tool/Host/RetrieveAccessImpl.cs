using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AgentCoreProcessor.Engine.Mind;
using AgentLilara.PluginSDK.Services;

namespace AgentCoreProcessor.Tool.Host
{
    /// <summary>
    /// IRetrieveAccess 桥接实现。映射内部 RetrieveService DTO 到 SDK。
    /// </summary>
    internal class RetrieveAccessImpl : IRetrieveAccess
    {
        private readonly RetrieveService _retrieve;

        public RetrieveAccessImpl(RetrieveService retrieve) => _retrieve = retrieve;

        public async Task<RetrieveResultDto> SearchAsync(string query, int limit = 20)
        {
            var result = await _retrieve.SearchAsync(query, limit);
            return new RetrieveResultDto
            {
                Hits = result.Hits.Select(h => new RetrieveHitDto
                {
                    Id = h.Id,
                    Kind = h.Kind,
                    Raw = h.Raw,
                    MatchCount = h.MatchCount,
                    Credibility = h.Credibility,
                    IsInference = h.IsInference,
                    UtteranceId = h.UtteranceId,
                    SubEvidence = h.SubEvidence,
                    OccurredAt = h.OccurredAt,
                }).ToList(),
                Query = result.Query,
                Timestamp = result.Timestamp,
            };
        }

        public async Task<List<RetrieveHitDto>> GetByKeywordAsync(string keyword, int limit = 20)
        {
            var hits = await _retrieve.GetByKeywordAsync(keyword, limit);
            return hits.Select(h => new RetrieveHitDto
            {
                Id = h.Id,
                Kind = h.Kind,
                Raw = h.Raw,
                MatchCount = h.MatchCount,
                Credibility = h.Credibility,
                IsInference = h.IsInference,
                UtteranceId = h.UtteranceId,
                SubEvidence = h.SubEvidence,
                OccurredAt = h.OccurredAt,
            }).ToList();
        }
    }
}
