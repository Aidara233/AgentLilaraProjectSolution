using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AgentCoreProcessor.Database;
using AgentLilara.PluginSDK.Services;

namespace AgentCoreProcessor.Tool.Host
{
    internal class ReviewAccessImpl : IReviewAccess
    {
        private readonly Engine.ReviewEngine _engine;
        private readonly Engine.ISystemContext _ctx;

        public ReviewAccessImpl(Engine.ReviewEngine engine, Engine.ISystemContext ctx)
        {
            _engine = engine;
            _ctx = ctx;
        }

        public int? CursorMessageId => _engine.CursorMessageId;
        public int? CursorChannelId => _engine.CursorChannelId;

        public void MoveCursor(int? messageId, int? channelId)
        {
            _engine.CursorMessageId = messageId;
            _engine.CursorChannelId = channelId;
        }

        public async Task<List<ReviewMessageDto>> BrowseAsync(int count)
        {
            if (_engine.CursorChannelId == null)
                return new List<ReviewMessageDto>();

            List<UserMessage> messages;
            if (_engine.CursorMessageId != null)
                messages = await _ctx.Session.GetMessagesAfterIdAsync(
                    _engine.CursorChannelId.Value, _engine.CursorMessageId.Value, count);
            else
                messages = await _ctx.Session.GetContextByChannelAsync(
                    _engine.CursorChannelId.Value, count);

            if (messages.Count > 0)
                _engine.CursorMessageId = messages.Last().Id;

            return messages.Select(ToDto).ToList();
        }

        public async Task<List<ReviewMessageDto>> SearchMessagesAsync(
            string? query, int? channelId, int? personId,
            string? timeStart, string? timeEnd, int limit)
        {
            // 搜索指定频道（或所有频道）
            if (channelId != null)
            {
                var messages = await _ctx.Session.SearchMessagesByChannelAsync(
                    channelId.Value, query, 0, limit);
                return messages.Select(ToDto).ToList();
            }

            // 跨频道搜索：遍历所有频道取结果
            var channels = await _ctx.Session.GetAllChannelsAsync();
            var results = new List<ReviewMessageDto>();
            foreach (var ch in channels)
            {
                if (results.Count >= limit) break;
                var msgs = await _ctx.Session.SearchMessagesByChannelAsync(
                    ch.Id, query, 0, limit - results.Count);
                results.AddRange(msgs.Select(ToDto));
            }
            return results.Take(limit).ToList();
        }

        public async Task<ReviewPersonDto?> GetPersonAsync(int personId)
        {
            var person = await _ctx.Session.GetPersonByIdAsync(personId);
            if (person == null) return null;

            var scores = await _ctx.EvaluationScores.GetByTargetAsync("person", personId);

            return new ReviewPersonDto
            {
                Id = person.Id,
                Name = person.Name ?? "",
                Aliases = person.Aliases,
                FastMemory = person.FastMemory,
                TrustLevel = person.TrustLevel.ToString(),
                AlertLevel = person.AlertLevel,
                Dimensions = scores.Select(s => new ReviewDimensionDto
                {
                    Dimension = s.Dimension,
                    Value = s.Value
                }).ToList()
            };
        }

        public async Task<List<ReviewBeaconDto>> GetUnprocessedBeaconsAsync()
        {
            var hints = await _ctx.ReviewHints.GetUnprocessedAsync();
            return hints.Select(h => new ReviewBeaconDto
            {
                Id = h.Id,
                MessageId = h.MessageId,
                ChannelId = h.ChannelId,
                PersonId = h.PersonId,
                Content = h.Content,
                Source = h.Source,
                CreatedAt = h.CreatedAt.ToString("MM-dd HH:mm")
            }).ToList();
        }

        public void AddEvaluation(string targetType, int targetId, string dimension, string rating)
        {
            _engine.EvaluationBuffer.Add(new Engine.EvaluationBufferEntry
            {
                TargetType = targetType,
                TargetId = targetId,
                Dimension = dimension,
                Rating = rating
            });
        }

        public string ThinkingNotes
        {
            get => _engine.ThinkingNotes;
            set => _engine.ThinkingNotes = value;
        }

        public void SaveProgress() => _engine.SaveProgress();
        public void ClearProgress() => _engine.ClearProgress();

        public Task LogActionAsync(string actionType, string summary, string? detailJson = null)
            => _engine.LogActionAsync(actionType, summary, detailJson);

        public void TrackChannel(int channelId) => _engine.ChannelsVisited.Add(channelId);
        public void TrackPerson(int personId) => _engine.PersonsEncountered.Add(personId);

        private static ReviewMessageDto ToDto(UserMessage msg) => new()
        {
            Id = msg.Id,
            Time = msg.Time.ToString("MM-dd HH:mm"),
            SenderName = msg.IsFromBot ? "Lilara"
                : !string.IsNullOrEmpty(msg.SenderName) ? msg.SenderName
                : $"U#{msg.UserId}",
            Content = msg.Content,
            IsFromBot = msg.IsFromBot
        };
    }
}
