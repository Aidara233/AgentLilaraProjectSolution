// AgentCoreProcessor/Engine/Context/IChannelDataSource.cs
using System.Collections.Generic;
using System.Threading.Tasks;
using AgentCoreProcessor.Database;

namespace AgentCoreProcessor.Engine.Context;

/// <summary>ChannelContextService 的窄数据访问接缝，便于单测注入假数据。</summary>
internal interface IChannelDataSource
{
    Task<List<UserMessage>> GetRecentMessagesAsync(int channelId, int limit);
    Task<User?> GetUserByIdAsync(int userId);
    Task<Person?> GetPersonByIdAsync(int personId);

    // ── 历史/引用/图片格式化所需（Task 4 新增）──

    /// <summary>拉取频道最近 limit 条消息（时间升序）。等价于 SessionManager.GetContextByChannelAsync。</summary>
    Task<List<UserMessage>> GetContextByChannelAsync(int channelId, int limit);

    /// <summary>拉取频道中 Id > afterId 的最近 limit 条消息（时间升序）。</summary>
    Task<List<UserMessage>> GetLatestMessagesAfterIdAsync(int channelId, int afterId, int limit);

    /// <summary>按平台消息ID查找单条消息。</summary>
    Task<UserMessage?> GetByPlatformMessageIdAsync(int channelId, string platformMessageId);

    /// <summary>以某条消息为锚点，取前后各 radius 条消息作为上下文。</summary>
    Task<List<UserMessage>> GetContextAroundAsync(int messageId, int channelId, int radius);

    /// <summary>获取 bot 在指定平台的平台ID（如 QQ 号）。</summary>
    string? GetBotPlatformId(string platform);
}
