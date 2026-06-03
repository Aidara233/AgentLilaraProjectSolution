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

            return await ToDtoListAsync(messages);
        }

        public async Task<List<ReviewMessageDto>> SearchMessagesAsync(
            string? query, int? channelId, int? personId,
            string? timeStart, string? timeEnd, int limit)
        {
            // 前置：按 person_id 解析 userIds
            List<int>? userIds = null;
            if (personId != null)
            {
                var users = await _ctx.Session.GetUsersByPersonIdAsync(personId.Value);
                userIds = users.Select(u => u.Id).ToList();
                if (userIds.Count == 0)
                    return new List<ReviewMessageDto>(); // 该人物无任何关联账号
            }

            // 前置：解析时间
            DateTime? dtStart = null, dtEnd = null;
            if (!string.IsNullOrEmpty(timeStart) && DateTime.TryParse(timeStart, out var ds))
                dtStart = ds.Date; // 从当天 00:00 开始
            if (!string.IsNullOrEmpty(timeEnd) && DateTime.TryParse(timeEnd, out var de))
                dtEnd = de.Date.AddDays(1).AddTicks(-1); // 包含整天到 23:59:59.999...

            // 频道列表
            List<int>? channelIds = channelId != null ? new List<int> { channelId.Value } : null;

            // 统一 SQL 搜索
            var messages = await _ctx.Session.SearchMessagesAsync(
                channelIds, query, userIds, dtStart, dtEnd, limit);

            return await ToDtoListAsync(messages);
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

        public async Task<List<PersonTraitDto>> GetPersonTraitsAsync(int personId, string? category = null)
        {
            var traits = category != null
                ? await _ctx.PersonTraits.GetByCategoryAsync(personId, category)
                : await _ctx.PersonTraits.GetByPersonAsync(personId);
            return traits.Select(t => new PersonTraitDto
            {
                Id = t.Id,
                PersonId = t.PersonId,
                Category = t.Category,
                Key = t.Key,
                Value = t.Value,
                Confidence = t.Confidence,
                SourceHint = t.SourceHint,
                UpdatedAt = t.UpdatedAt.ToString("yyyy-MM-dd HH:mm")
            }).ToList();
        }

        public async Task UpsertPersonTraitAsync(int personId, string category, string key,
            string value, float confidence, string? sourceHint = null)
        {
            await _ctx.PersonTraits.UpsertAsync(personId, category, key, value, confidence, sourceHint ?? "");
        }

        public Task<TrustCriteriaDto> GetTrustCriteriaAsync(int personId)
            => _engine.GetTrustCriteriaAsync(personId);

        public async Task<ReviewMessageDto?> GetMessageByIdAsync(int messageId)
        {
            var msg = await _ctx.Session.GetMessageByIdAsync(messageId);
            if (msg == null) return null;
            var dtos = await ToDtoListAsync(new List<UserMessage> { msg });
            return dtos.FirstOrDefault();
        }

        private async Task<List<ReviewMessageDto>> ToDtoListAsync(List<UserMessage> messages)
        {
            // 批量解析 PersonId
            var userIds = messages.Select(m => m.UserId).Distinct().ToList();
            var personMap = new Dictionary<int, int?>(); // userId -> personId
            foreach (var uid in userIds)
            {
                var user = await _ctx.Session.GetUserByIdAsync(uid);
                personMap[uid] = user?.PersonId;
            }

            return messages.Select(m =>
            {
                personMap.TryGetValue(m.UserId, out var personId);
                return new ReviewMessageDto
                {
                    Id = m.Id,
                    PlatformMessageId = m.PlatformMessageId,
                    ChannelId = m.ChannelId,
                    Time = m.Time.ToString("MM-dd HH:mm"),
                    SenderName = m.IsFromBot ? "Lilara"
                        : !string.IsNullOrEmpty(m.SenderName) ? m.SenderName
                        : $"U#{m.UserId}",
                    PersonId = personId,
                    Content = m.Content,
                    IsFromBot = m.IsFromBot
                };
            }).ToList();
        }
    }
}
