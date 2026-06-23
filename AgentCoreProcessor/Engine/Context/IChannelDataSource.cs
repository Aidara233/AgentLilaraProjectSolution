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
}
