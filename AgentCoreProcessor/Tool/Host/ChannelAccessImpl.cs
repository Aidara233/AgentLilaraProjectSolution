using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AgentCoreProcessor.Engine;
using AgentLilara.PluginSDK.Services;

namespace AgentCoreProcessor.Tool.Host
{
    internal class ChannelAccessImpl : IChannelAccess
    {
        private readonly ISystemContext _ctx;

        public ChannelAccessImpl(ISystemContext ctx)
        {
            _ctx = ctx;
        }

        public void NotifyChannel(int channelId, string content)
        {
            _ctx.NotifyChannel(channelId, content);
        }

        public async Task<List<ChannelSummary>> GetAllChannelsAsync()
        {
            var channels = await _ctx.Session.GetAllChannelsAsync();
            return channels.Select(c => new ChannelSummary
            {
                Id = c.Id,
                Name = c.Name,
                Platform = "",
                MessageCount = 0,
                HasActiveEngine = _ctx.HasActiveEngine($"Channel:{c.Id}")
            }).ToList();
        }

        public async Task<ChannelDetail?> GetChannelDetailAsync(int channelId)
        {
            var channels = await _ctx.Session.GetAllChannelsAsync();
            var ch = channels.FirstOrDefault(c => c.Id == channelId);
            if (ch == null) return null;
            return new ChannelDetail
            {
                Id = ch.Id,
                Name = ch.Name,
                Platform = "",
                PlatformChannelId = ""
            };
        }

        public async Task<List<MessageSummary>> GetMessagesAsync(int channelId, int count = 20)
        {
            var messages = await _ctx.Session.GetContextByChannelAsync(channelId, limit: count);
            return messages.Select(m => new MessageSummary
            {
                Id = m.Id,
                UserName = m.IsFromBot ? "Lilara"
                    : !string.IsNullOrEmpty(m.SenderName) ? m.SenderName
                    : $"User:{m.UserId}",
                Content = m.Content,
                Timestamp = m.Time
            }).ToList();
        }

        public async Task UpdateAffinityAsync(int channelId, float delta)
        {
            var channels = await _ctx.Session.GetAllChannelsAsync();
            var ch = channels.FirstOrDefault(c => c.Id == channelId);
            if (ch == null) return;
            ch.Affinity = System.Math.Clamp(ch.Affinity + delta, 0.1f, 3.0f);
            await _ctx.Session.UpdateChannelAsync(ch);
        }
    }
}
