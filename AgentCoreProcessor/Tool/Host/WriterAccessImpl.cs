using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AgentCoreProcessor.Database;
using AgentCoreProcessor.Engine.Mind;
using AgentLilara.PluginSDK.Services;

namespace AgentCoreProcessor.Tool.Host
{
    /// <summary>
    /// IWriterAccess 桥接实现。映射 SDK WriteBatch/Claim/Journal DTO 到内部 Writer。
    /// </summary>
    internal class WriterAccessImpl : IWriterAccess
    {
        private readonly Writer _writer;

        public WriterAccessImpl(Writer writer) => _writer = writer;

        public async Task<List<int>> WriteBatchAsync(WriteBatchDto batch)
        {
            var req = new WriteBatchRequest
            {
                Claims = batch.Claims.Select(ToInternal).ToList(),
                Journals = batch.Journals.Select(ToInternal).ToList(),
            };
            return await _writer.WriteBatchAsync(req);
        }

        public async Task<int> WriteClaimAsync(ClaimDto claim)
        {
            var req = new WriteBatchRequest
            {
                Claims = new List<WriteClaimRequest> { ToInternal(claim) },
            };
            var affected = await _writer.WriteBatchAsync(req);
            return affected.FirstOrDefault();
        }

        public async Task<int> WriteJournalAsync(JournalDto journal)
        {
            var req = new WriteBatchRequest
            {
                Journals = new List<WriteJournalRequest> { ToInternal(journal) },
            };
            var affected = await _writer.WriteBatchAsync(req);
            return affected.FirstOrDefault();
        }

        private static WriteClaimRequest ToInternal(ClaimDto dto) => new()
        {
            Subject = dto.Subject != null ? new ClaimSlot { Type = dto.Subject.Type, Index = dto.Subject.Index, Ref = dto.Subject.Ref } : null,
            Predicate = dto.Predicate != null ? new ClaimSlot { Type = dto.Predicate.Type, Index = dto.Predicate.Index, Ref = dto.Predicate.Ref } : null,
            Object = dto.Object != null ? new ClaimSlot { Type = dto.Object.Type, Index = dto.Object.Index, Ref = dto.Object.Ref } : null,
            Raw = dto.Raw,
            SourceJson = dto.SourceJson,
            UtteranceId = dto.UtteranceId,
            Strength = dto.Strength,
            Credibility = dto.Credibility,
            IsInference = dto.IsInference,
        };

        private static WriteJournalRequest ToInternal(JournalDto dto) => new()
        {
            Content = dto.Content,
            Kind = dto.Kind,
            Source = dto.Source,
            ChannelId = dto.ChannelId,
            SessionId = dto.SessionId,
            EmotionSignalsJson = dto.EmotionSignalsJson,
            OccurredAt = dto.OccurredAt,
        };
    }
}
