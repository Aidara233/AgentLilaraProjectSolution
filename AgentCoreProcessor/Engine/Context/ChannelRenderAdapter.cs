using AgentCoreProcessor.Database;
using AgentCoreProcessor.Models;

namespace AgentCoreProcessor.Engine.Context;

internal static class ChannelRenderMapping
{
    internal static ChannelMessageSnapshot Snapshot(UserMessage message) => new()
    {
        Id = message.Id, UserId = message.UserId, ChannelId = message.ChannelId,
        Content = message.Content, SenderName = message.SenderName, Time = message.Time,
        IsFromBot = message.IsFromBot, PlatformMessageId = message.PlatformMessageId,
        ReplyToPlatformMessageId = message.ReplyToPlatformMessageId,
        ImageHashes = message.ImageHashes, MentionedPlatformIds = message.MentionedPlatformIds
    };

    internal static ContentPart ModelPart(ChannelRenderedPart part) => new()
    {
        Type = part.Type, Text = part.Text, ImagePath = part.ImagePath
    };

    internal static Message ModelMessage(ChannelRenderedMessage message) => new()
    {
        Role = message.Role, Content = message.Content,
        ContentParts = message.ContentParts?.Select(ModelPart).ToList()
    };
}

internal sealed class ChannelRenderSourceAdapter(IChannelDataSource source) : IChannelRenderSource
{
    private readonly ParticipantSourceAdapter _participants = new(source);
    public Task<IReadOnlyList<ParticipantMessage>> GetRecentAsync(int channelId, int limit) => _participants.GetRecentAsync(channelId, limit);
    public Task<ParticipantUser?> GetUserAsync(int userId) => _participants.GetUserAsync(userId);
    public Task<ParticipantPerson?> GetPersonAsync(int personId) => _participants.GetPersonAsync(personId);
    public async Task<List<ChannelMessageSnapshot>> GetContextByChannelAsync(int channelId, int limit) =>
        (await source.GetContextByChannelAsync(channelId, limit)).Select(ChannelRenderMapping.Snapshot).ToList();
    public async Task<List<ChannelMessageSnapshot>> GetLatestMessagesAfterIdAsync(int channelId, int afterId, int limit) =>
        (await source.GetLatestMessagesAfterIdAsync(channelId, afterId, limit)).Select(ChannelRenderMapping.Snapshot).ToList();
    public async Task<ChannelMessageSnapshot?> GetByPlatformMessageIdAsync(int channelId, string platformMessageId)
    {
        var message = await source.GetByPlatformMessageIdAsync(channelId, platformMessageId);
        return message is null ? null : ChannelRenderMapping.Snapshot(message);
    }
    public async Task<List<ChannelMessageSnapshot>> GetContextAroundAsync(int messageId, int channelId, int radius) =>
        (await source.GetContextAroundAsync(messageId, channelId, radius)).Select(ChannelRenderMapping.Snapshot).ToList();
    public string? GetBotPlatformId(string platform) => source.GetBotPlatformId(platform);
}
