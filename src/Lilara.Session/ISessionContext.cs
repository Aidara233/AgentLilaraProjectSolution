using AgentCoreProcessor.Database;
using AgentCoreProcessor.Engine.Context;
using AgentCoreProcessor.Models;

namespace AgentCoreProcessor.Engine.Session;

/// <summary>Session's context input port; rendering and source access belong to the host implementation.</summary>
internal interface ISessionContext
{
    IEnumerable<int> ChannelIds { get; }
    ChannelContextState StateFor(int channelId);
    ChannelContextState AttachChannel(int channelId, string? channelName);
    void DetachChannel(int channelId);
    Task<List<Message>> BuildChannelContextAsync(int channelId);
    Task<List<Message>> BuildStartAsync();
    Task<List<Message>> BuildRoundAsync(IEnumerable<(int channelId, UserMessage msg, bool isMention)> drained);
}
