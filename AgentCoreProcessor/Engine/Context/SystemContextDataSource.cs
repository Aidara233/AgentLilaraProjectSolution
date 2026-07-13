// AgentCoreProcessor/Engine/Context/SystemContextDataSource.cs
using System.Collections.Generic;
using System.Threading.Tasks;
using AgentCoreProcessor.Database;
using AgentCoreProcessor.Engine;

namespace AgentCoreProcessor.Engine.Context;

/// <summary>
/// 生产数据源适配器：将 IChannelDataSource 的所有方法转发到 ISystemContext。
/// 供 ExpressEngine 构造 ChannelContextService 时使用。
/// </summary>
internal sealed class SystemContextDataSource : IChannelDataSource
{
    private readonly ISystemContext _ctx;

    public SystemContextDataSource(ISystemContext ctx) => _ctx = ctx;

    // IChannelDataSource.GetRecentMessagesAsync → ctx.Session.GetContextByChannelAsync (降序，不翻转)
    // GetRecentMessagesAsync 用于参与者派生，需要最近消息；
    // 等价于 messages.GetRecentByChannelAsync (降序)，但 SessionManager 公开的是 GetContextByChannelAsync (升序翻转后)
    // 这里我们直接用 GetContextByChannelAsync 返回升序列表，倒序取最新 limit 条已足够（参与者派生不依赖顺序）
    public Task<List<UserMessage>> GetRecentMessagesAsync(int channelId, int limit)
        => _ctx.Session.GetContextByChannelAsync(channelId, limit);

    public Task<User?> GetUserByIdAsync(int userId)
        => _ctx.Session.GetUserByIdAsync(userId);

    public Task<Person?> GetPersonByIdAsync(int personId)
        => _ctx.Session.GetPersonByIdAsync(personId);

    public Task<List<UserMessage>> GetContextByChannelAsync(int channelId, int limit)
        => _ctx.Session.GetContextByChannelAsync(channelId, limit);

    public Task<List<UserMessage>> GetLatestMessagesAfterIdAsync(int channelId, int afterId, int limit)
        => _ctx.Session.GetLatestMessagesAfterIdAsync(channelId, afterId, limit);

    public Task<UserMessage?> GetByPlatformMessageIdAsync(int channelId, string platformMessageId)
        => _ctx.Session.GetByPlatformMessageIdAsync(channelId, platformMessageId);

    public Task<List<UserMessage>> GetContextAroundAsync(int messageId, int channelId, int radius)
        => _ctx.Session.GetContextAroundAsync(messageId, channelId, radius);

    public string? GetBotPlatformId(string platform)
        => _ctx.Adapters.GetBotPlatformId(platform);
}
